﻿using Dalamud.Plugin;
using ImGuiNET;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace BrowserHost.Plugin
{
	class Dependency
	{
		public string Url;
		public string Version;
		public string Directory;
		public string Checksum;
	}

	class DependencyManager : IDisposable
	{
		private static string downloadDir = "downloads";
		private static Dependency[] dependencies = new[]
		{
			new Dependency()
			{
				Url = "https://github.com/ackwell/BrowserHost/releases/download/cef-binaries/cefsharp-{VERSION}.zip",
				Directory = "cef",
				Version = "81.3.10+gb223419+chromium-81.0.4044.138",
				Checksum = "02B06F5D7015493AD468C1548C237914C127506B3D3EDF9C3DDD4EB926B3F8CE",
			}
		};

		public event EventHandler DependenciesReady;

		private string dependencyDir;
		private Dependency[] missingDependencies;
		private ConcurrentDictionary<string, float> installProgress = new ConcurrentDictionary<string, float>();

		private enum ViewMode
		{
			Confirm,
			Installing,
			Complete,
			Failed,
			Hidden,
		}
		private ViewMode viewMode = ViewMode.Hidden;

		public DependencyManager(string pluginDir)
		{
			// We're storing dependencies a level above the plugin so they get preserved across plugin updates
			dependencyDir = Path.GetDirectoryName(pluginDir);
		}

		public void Initialise()
		{
			CheckDependencies();
		}

		public void Dispose() { }

		private void CheckDependencies()
		{
			missingDependencies = dependencies.Where(DependencyMissing).ToArray();
			if (missingDependencies.Length == 0)
			{
				viewMode = ViewMode.Hidden;
				DependenciesReady?.Invoke(this, null);
			}
			else
			{
				viewMode = ViewMode.Confirm;
			}
		}

		private bool DependencyMissing(Dependency dependency)
		{
			var versionFilePath = Path.Combine(GetDependencyPath(dependency), "VERSION");

			string versionContents;
			try { versionContents = File.ReadAllText(versionFilePath); }
			catch { return true; }

			return !versionContents.Contains(dependency.Version);
		}

		private void InstallDependencies()
		{
			viewMode = ViewMode.Installing;
			PluginLog.Log("Installing dependencies...");

			var installTasks = missingDependencies.Select(InstallDependency);
			Task.WhenAll(installTasks).ContinueWith(task =>
			{
				var failed = installProgress.Any(pair => pair.Value < 0);
				viewMode = failed ? ViewMode.Failed : ViewMode.Complete;
				PluginLog.Log($"Dependency install {viewMode}.");

				try { Directory.Delete(Path.Combine(dependencyDir, downloadDir), true); }
				catch { }
			});
		}

		private async Task InstallDependency(Dependency dependency)
		{
			PluginLog.Log($"Downloading {dependency.Directory} {dependency.Version}");

			// Ensure the downloads dir exists
			var downloadDir = Path.Combine(dependencyDir, DependencyManager.downloadDir);
			Directory.CreateDirectory(downloadDir);

			// Get the file name we'll download to - if it's already in downloads, it may be corrupt, delete
			var filePath = Path.Combine(downloadDir, $"{dependency.Directory}-{dependency.Version}.zip");
			File.Delete(filePath);

			// Set up the download and kick it off
			using WebClient client = new WebClient();
			client.DownloadProgressChanged += (sender, args) => installProgress.AddOrUpdate(
				dependency.Directory,
				args.ProgressPercentage,
				(key, oldValue) => Math.Max(oldValue, args.ProgressPercentage));
			await client.DownloadFileTaskAsync(
				dependency.Url.Replace("{VERSION}", dependency.Version),
				filePath);

			// Calculate the checksum for the download
			string downloadedChecksum;
			try
			{
				using (var sha = SHA256.Create())
				using (var stream = new FileStream(filePath, FileMode.Open))
				{
					stream.Position = 0;
					var rawHash = sha.ComputeHash(stream);
					var builder = new StringBuilder(rawHash.Length);
					for (var i = 0; i < rawHash.Length; i++) { builder.Append($"{rawHash[i]:X2}"); }
					downloadedChecksum = builder.ToString();
				}
			}
			catch
			{
				PluginLog.LogError($"Failed to calculate checksum for {filePath}");
				downloadedChecksum = "FAILED";
			}

			// Make sure the checksum matches
			if (downloadedChecksum != dependency.Checksum)
			{
				PluginLog.LogError($"Mismatched checksum for {filePath}");
				installProgress.AddOrUpdate(dependency.Directory, -1, (key, oldValue) => -1);
				File.Delete(filePath);
				return;
			}

			// Extract to the destination dir
			var destinationDir = GetDependencyPath(dependency);
			try { Directory.Delete(destinationDir, true); }
			catch { }
			ZipFile.ExtractToDirectory(filePath, destinationDir);

			// Clear out the downloaded file now we're done with it
			File.Delete(filePath);
		}

		public string GetDependencyPathFor(string dependencyDir)
		{
			var dependency = dependencies.First(dependency => dependency.Directory == dependencyDir);
			if (dependency == null) { throw new Exception($"Unknown dependency {dependencyDir}"); }
			return GetDependencyPath(dependency);
		}

		private string GetDependencyPath(Dependency dependency)
		{
			return Path.Combine(dependencyDir, dependency.Directory);
		}

		public void Render()
		{
			if (viewMode == ViewMode.Hidden) { return; }

			var windowFlags = ImGuiWindowFlags.AlwaysAutoResize;
			ImGui.Begin("BrowserHost dependencies", windowFlags);
			ImGui.SetWindowFocus();

			switch (viewMode)
			{
				case ViewMode.Confirm: RenderConfirm(); break;
				case ViewMode.Installing: RenderInstalling(); break;
				case ViewMode.Complete: RenderComplete(); break;
				case ViewMode.Failed: RenderFailed(); break;
			}

			ImGui.End();
		}

		private void RenderConfirm()
		{
			ImGui.Text($"The following dependencies are currently missing:");

			if (missingDependencies == null) { return; }

			ImGui.Indent();
			foreach (var dependency in missingDependencies)
			{
				ImGui.Text($"{dependency.Directory} ({dependency.Version})");
			}
			ImGui.Unindent();

			ImGui.Separator();

			if (ImGui.Button("Install missing dependencies")) { InstallDependencies(); }
		}

		private void RenderInstalling()
		{
			ImGui.Text("Installing dependencies...");

			ImGui.Separator();

			var progressSize = new Vector2(200, 0);

			foreach (var progress in installProgress)
			{
				if (progress.Value >= 100) { ImGui.ProgressBar(progress.Value / 100, progressSize, "Extracting"); }
				else if (progress.Value < 0)
				{
					ImGui.PushStyleColor(ImGuiCol.PlotHistogram, 0xAA0000FF);
					ImGui.ProgressBar(1, progressSize, "Error");
					ImGui.PopStyleColor();
				}
				else { ImGui.ProgressBar(progress.Value / 100, progressSize); }
				ImGui.SameLine();
				ImGui.Text(progress.Key);
			}
		}

		private void RenderComplete()
		{
			ImGui.Text("Dependency installation complete!");
			if (ImGui.Button("OK", new Vector2(100, 0))) { CheckDependencies(); }
		}

		private void RenderFailed()
		{
			ImGui.Text("One or more dependencies failed to install successfully.");
			ImGui.Text("If this keeps happening, let us known on discord.");
			if (ImGui.Button("Retry", new Vector2(100, 0))) { CheckDependencies(); }
		}
	}
}