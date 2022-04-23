﻿// © Anamnesis.
// Licensed under the MIT license.

namespace Anamnesis.Updater
{
	using System;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.IO;
	using System.IO.Compression;
	using System.Net;
	using System.Net.Http;
	using System.Text.Json;
	using System.Text.Json.Serialization;
	using System.Threading.Tasks;
	using Anamnesis.GUI.Dialogs;
	using Anamnesis.Services;
	using XivToolsWpf;

	public class UpdateService : ServiceBase<UpdateService>
	{
		private const string StableRepository = "imchillin/Anamnesis";

		private readonly HttpClient httpClient = new HttpClient();
		private Release? currentRelease;

		private static string UpdateTempDir => Path.GetTempPath() + "/AnamnesisUpdateLatest/";

		public override async Task Initialize()
		{
			await base.Initialize();

			bool skipTimeCheck = false;

			// Determine if this is a dev build
			if (VersionInfo.Date.Year <= 2000)
			{
				// Always skip the time check if they say no
				skipTimeCheck = await PromptUserWithDevBuildWarning();
			}

			DateTimeOffset lastCheck = SettingsService.Current.LastUpdateCheck;
			TimeSpan elapsed = DateTimeOffset.Now - lastCheck;
			if (elapsed.TotalHours < 6 && !skipTimeCheck)
			{
				Log.Information("Last update check was less than 6 hours ago. Skipping.");
				return;
			}

			await this.CheckForUpdates();
		}

		public async Task<bool> CheckForUpdates()
		{
			if (Directory.Exists(UpdateTempDir))
				Directory.Delete(UpdateTempDir, true);

			if (!this.httpClient.DefaultRequestHeaders.Contains("User-Agent"))
				this.httpClient.DefaultRequestHeaders.Add("User-Agent", "AutoUpdater");

			if (!this.httpClient.DefaultRequestHeaders.Contains("Accept"))
				this.httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");

			try
			{
				string url = $"https://api.github.com/repos/{StableRepository}/releases/latest";
				string result = await this.httpClient.GetStringAsync(url);
				this.currentRelease = JsonSerializer.Deserialize<Release>(result);

				if (this.currentRelease == null)
					throw new Exception("Failed to deserialize json response");

				if (this.currentRelease.Published == null)
					throw new Exception("No published timestamp in update json");

				bool isPreAlpha = (this.currentRelease.TagName != null) && this.currentRelease.TagName.Contains("pre-alpha");
				DateTimeOffset published = (DateTimeOffset)this.currentRelease.Published;
				published = published.ToUniversalTime();

				// Bump the published time down by a few hours to account for release upload times.
				published = published.AddHours(-4);

				bool update = published > VersionInfo.Date;
				if (isPreAlpha)
				{
					await PromptUserWithDevBuildWarning(); // prompt the user and warn about dev builds

					if (SettingsService.Current.UseAnamDevStream)
					{
						if (update)
						{
							await Dispatch.MainThread();

							UpdateDialog dlg = new();
							dlg.Changes = this.currentRelease.Changes;
							await ViewService.ShowDialog<UpdateDialog, bool?>("Update", dlg);
							SettingsService.Current.LastUpdateCheck = DateTimeOffset.Now;
							SettingsService.Save();
							return update;
						}
					}
					else
					{
						return false; // It's a pre-alpha build and the user doesn't want to use dev builds
					}
				}
				else if (!SettingsService.Current.UseAnamDevStream)
				{
					if(!isPreAlpha && update)
					{
						await Dispatch.MainThread();

						UpdateDialog dlg = new();
						dlg.Changes = this.currentRelease.Changes;
						await ViewService.ShowDialog<UpdateDialog, bool?>("Update", dlg);
						SettingsService.Current.LastUpdateCheck = DateTimeOffset.Now;
						SettingsService.Save();
						return update;
					}
					else
					{
						return false;
					}
				}

				return false;
			}
			catch (HttpRequestException ex)
			{
				// 404 errors just mean there are no latest releases.
				if (ex.StatusCode == HttpStatusCode.NotFound)
				{
					SettingsService.Current.LastUpdateCheck = DateTimeOffset.Now;
					SettingsService.Save();
					return false;
				}

				Log.Error(ex, "Failed to complete update check");
				return false;
			}
			catch (Exception ex)
			{
				Log.Error(ex, "Failed to complete update check");
				return false;
			}
		}

		public async Task DoUpdate(Action<double>? updateProgress = null)
		{
			try
			{
				string? currentExePath = Environment.GetCommandLineArgs()[0];

				if (string.IsNullOrEmpty(currentExePath))
					throw new Exception("Unable to determine current assembly path");

				currentExePath = currentExePath.Replace(".dll", ".exe");
				if (!File.Exists(currentExePath))
					throw new Exception("Unable to determine current executable path");

				if (this.currentRelease == null)
					throw new Exception("No release to download");

				if (this.currentRelease.Assets == null)
					throw new Exception("No assets in release");

				Release.Asset? asset = null;
				foreach (Release.Asset tAsset in this.currentRelease.Assets)
				{
					if (tAsset.Name == null)
						continue;

					if (!tAsset.Name.EndsWith(".zip", StringComparison.InvariantCulture))
						continue;

					asset = tAsset;
				}

				if (asset == null)
					throw new Exception("Failed to find asset for release");

				if (asset.Url == null)
					throw new Exception("Release asset has no url");

				// Download asset to temp file
				string zipFilePath = Path.GetTempFileName();
				using WebClient client = new();
				if (updateProgress != null)
				{
					client.DownloadProgressChanged += (object sender, DownloadProgressChangedEventArgs e) =>
					{
						updateProgress.Invoke(e.ProgressPercentage / 100.0);
					};
				}

				await client.DownloadFileTaskAsync(asset.Url, zipFilePath);

				if (!Directory.Exists(UpdateTempDir))
					Directory.CreateDirectory(UpdateTempDir);

				using FileStream zipFile = new(zipFilePath, FileMode.Open);
				using ZipArchive archive = new(zipFile, ZipArchiveMode.Read);
				archive.ExtractToDirectory(UpdateTempDir, true);
				archive.Dispose();
				await zipFile.DisposeAsync();

				// Remove temp file
				File.Delete(zipFilePath);

				// While testing, do not copy the update files over our working files.
				if (Debugger.IsAttached)
				{
					Directory.Delete(UpdateTempDir, true);
					string[] paths = Directory.GetFiles(".", "*.*", SearchOption.AllDirectories);
					foreach (string path in paths)
					{
						string dest = path.Replace(".\\", UpdateTempDir + "\\");

						string? dir = Path.GetDirectoryName(dest);
						if (dir != null && !Directory.Exists(dir))
							Directory.CreateDirectory(dir);

						File.Copy(path, dest, true);
					}
				}

				// Start the update extractor
				string currentDir = Directory.GetCurrentDirectory();
				string procName = Process.GetCurrentProcess().ProcessName;
				ProcessStartInfo start = new(UpdateTempDir + "/Updater/UpdateExtractor.exe", $"\"{currentDir}\" {procName}");
				Process.Start(start);

				// Shutdown anamnesis
				App.Current.Shutdown();
				return;
			}
			catch (Exception ex)
			{
				Log.Error(ex, "Failed to perform update");
			}
		}

		private static async Task<bool> PromptUserWithDevBuildWarning()
		{
			// Don't show if there is a debugger attached
			if (Debugger.IsAttached)
				return false;

			// Prompt the user
			var result = await GenericDialog.ShowLocalizedAsync("DevBuild_Body", "DevBuild_Title", System.Windows.MessageBoxButton.YesNo);
			if (result == true)
				return false;

			// Always skip the time check if they say no
			return true;
		}

		public class Release
		{
			[JsonPropertyName("tag_name")]
			public string? TagName { get; set; }

			[JsonPropertyName("published_at")]
			public DateTimeOffset? Published { get; set; }

			[JsonPropertyName("body")]
			public string? Changes { get; set; }

			[JsonPropertyName("assets")]
			public List<Asset>? Assets { get; set; }

			public class Asset
			{
				[JsonPropertyName("browser_download_url")]
				public string? Url { get; set; }

				[JsonPropertyName("name")]
				public string? Name { get; set; }
			}
		}
	}
}
