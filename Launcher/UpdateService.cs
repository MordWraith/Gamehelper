namespace Launcher
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Security.Cryptography;
    using System.Threading;
    using System.Threading.Tasks;
    using Newtonsoft.Json.Linq;
    using Shared.UpdateSecurity;

    internal static class UpdateService
    {
        private static HttpClient HttpClient = CreateClient(new UpdateProxyOptions());
        private static UpdateProxyOptions proxyOptions = new();

        internal static HttpClient SharedHttpClient => HttpClient;

        internal static UpdateProxyOptions ProxyOptions => proxyOptions;

        private static string? stagedVersion;
        private static string? stagedInstallDir;

        internal static void ConfigureProxy(UpdateProxyOptions options)
        {
            HttpClient newClient;
            try
            {
                newClient = CreateClient(options);
            }
            catch (Exception ex)
            {
                LauncherLog.Write($"Update proxy ignored: {ex.Message}");
                options = new UpdateProxyOptions();
                newClient = CreateClient(options);
            }

            proxyOptions = options;
            var oldClient = HttpClient;
            HttpClient = newClient;
            oldClient.Dispose();
            LauncherLog.Write($"Update proxy: {options.DisplayName()}");
        }

        private static HttpClient CreateClient(UpdateProxyOptions options)
        {
            var networkProxy = options.GetNetworkProxyUri();
            var client = networkProxy == null
                ? new HttpClient()
                : new HttpClient(new HttpClientHandler
                {
                    Proxy = new WebProxy(networkProxy),
                    UseProxy = true,
                });

            client.DefaultRequestHeaders.Add("User-Agent", "GameHelper-Updater/1.1");
            client.Timeout = TimeSpan.FromMinutes(10);
            return client;
        }

        internal static async Task<UpdateCheckResult> CheckForUpdateAsync(string appExePath, string installDir)
        {
            stagedInstallDir = installDir;
            var latestManifest = await DownloadManifestAsync();
            if (latestManifest == null)
            {
                LauncherLog.Write("Update check: manifest unavailable (download or signature verification failed).");
                return new UpdateCheckResult
                {
                    ErrorMessage = LauncherLocalization.L(
                        "Update manifest could not be reached or verified. Configure Proxy and retry, or start without update.",
                        "Update-Manifest konnte nicht erreicht oder geprueft werden. Proxy konfigurieren und erneut versuchen oder ohne Update starten."),
                };
            }

            var currentVersion = GetCurrentVersion(appExePath);
            UpdateMigrationNotice.TryRead(latestManifest, out var latestMigration);
            var manifest = await ResolveManifestForClientAsync(latestManifest, currentVersion, latestMigration)
                ?? latestManifest;

            return EvaluateManifest(manifest, appExePath, installDir, currentVersion);
        }

        private static async Task<JObject?> ResolveManifestForClientAsync(
            JObject latestManifest,
            string currentVersion,
            UpdateMigrationNotice.Info? latestMigration)
        {
            if (latestMigration == null ||
                !UpdateMigrationNotice.AppliesToVersion(currentVersion, latestMigration) ||
                string.IsNullOrWhiteSpace(latestMigration.MaxAutoUpdateVersion))
            {
                return null;
            }

            var latestVersion = latestManifest["version"]?.ToString() ?? string.Empty;
            if (!VersionCompare.IsGreaterOrEqual(latestVersion, latestMigration.ManualInstallVersion))
            {
                return null;
            }

            if (!VersionCompare.IsGreater(latestMigration.MaxAutoUpdateVersion, currentVersion))
            {
                return null;
            }

            if (VersionCompare.EqualsNormalized(latestVersion, latestMigration.MaxAutoUpdateVersion))
            {
                var files = latestManifest["files"] as JArray;
                if (files != null && files.Count > 0)
                {
                    return null;
                }
            }

            LauncherLog.Write(
                $"Update check: bridge v{currentVersion} -> v{latestMigration.MaxAutoUpdateVersion} " +
                $"(latest release v{latestVersion} needs manual install from v{latestMigration.ManualInstallVersion}).");

            return await DownloadManifestForVersionAsync(latestMigration.MaxAutoUpdateVersion);
        }

        private static UpdateCheckResult EvaluateManifest(
            JObject manifest,
            string appExePath,
            string installDir,
            string currentVersion)
        {
            var remoteVersion = manifest["version"]?.ToString();
            if (string.IsNullOrEmpty(remoteVersion))
            {
                LauncherLog.Write("Update check: manifest has no version field.");
                return new UpdateCheckResult();
            }

            UpdateMigrationNotice.TryRead(manifest, out var migrationNotice);
            var migration = migrationNotice != null &&
                            UpdateMigrationNotice.AppliesToVersion(currentVersion, migrationNotice)
                ? migrationNotice
                : null;
            var remotePublished = manifest["published"]?.ToString() ?? string.Empty;
            var localState = UpdateStateHelper.Load(installDir);
            var remoteIsNewerVersion = IsNewerVersion(remoteVersion, currentVersion);

            if (!remoteIsNewerVersion && !UpdateStateHelper.IsRemoteManifestNewer(remotePublished, localState))
            {
                LauncherLog.Write(
                    $"Update check: no new release (installed v{currentVersion}, remote v{remoteVersion}, published {remotePublished}).");
                return new UpdateCheckResult { MigrationNotice = migration };
            }

            if (UpdateZipPackage.TryRead(manifest, out var zipPackage))
            {
                if (!remoteIsNewerVersion &&
                    !string.IsNullOrEmpty(localState?.PackageHash) &&
                    localState.PackageHash.Equals(zipPackage.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    LauncherLog.Write(
                        $"Update check: ZIP package already installed (v{remoteVersion}, hash match).");
                    UpdateStateHelper.Save(installDir, remotePublished, remoteVersion, zipPackage.Hash);
                    return new UpdateCheckResult { MigrationNotice = migration };
                }

                if (migration != null &&
                    VersionCompare.IsGreaterOrEqual(remoteVersion, migration.ManualInstallVersion) &&
                    UpdateMigrationNotice.AppliesToVersion(currentVersion, migration))
                {
                    LauncherLog.Write(
                        $"Update check: v{remoteVersion} requires manual install from v{currentVersion} (>= v{migration.ManualInstallVersion}).");
                    return new UpdateCheckResult { MigrationNotice = migration };
                }

                LauncherLog.Write(
                    $"Update check: v{currentVersion} -> v{remoteVersion}, full package {zipPackage.Name}.");

                return new UpdateCheckResult
                {
                    Offer = new UpdateOffer
                    {
                        CurrentVersion = currentVersion,
                        RemoteVersion = remoteVersion,
                        RemotePublished = remotePublished,
                        Manifest = manifest,
                        FilesToDownload = Array.Empty<UpdateFileEntry>(),
                        ZipPackage = zipPackage,
                        MigrationNotice = migration,
                        Changelog = ParseChangelog(manifest),
                    },
                    MigrationNotice = migration,
                };
            }

            var files = manifest["files"] as JArray;
            if (files == null || files.Count == 0)
            {
                LauncherLog.Write("Update check: manifest contains no package or files.");
                if (migration != null &&
                    VersionCompare.IsGreaterOrEqual(remoteVersion, migration.ManualInstallVersion))
                {
                    return new UpdateCheckResult { MigrationNotice = migration };
                }

                return new UpdateCheckResult { MigrationNotice = migration };
            }

            var toDownload = BuildFilesToDownload(manifest, installDir);
            if (toDownload.Count == 0)
            {
                LauncherLog.Write(
                    $"Update check: all {files.Count} manifest files already match (v{remoteVersion}).");
                UpdateStateHelper.Save(installDir, remotePublished, remoteVersion);
                return new UpdateCheckResult { MigrationNotice = migration };
            }

            LauncherLog.Write(
                $"Update check: v{currentVersion} -> v{remoteVersion}, {toDownload.Count} file(s) to download.");

            return new UpdateCheckResult
            {
                Offer = new UpdateOffer
                {
                    CurrentVersion = currentVersion,
                    RemoteVersion = remoteVersion,
                    RemotePublished = remotePublished,
                    Manifest = manifest,
                    FilesToDownload = toDownload,
                    MigrationNotice = migration,
                    Changelog = ParseChangelog(manifest),
                },
                MigrationNotice = migration,
            };
        }

        private static List<UpdateFileEntry> BuildFilesToDownload(JObject manifest, string installDir)
        {
            var toDownload = new List<UpdateFileEntry>();
            if (manifest["files"] is not JArray files)
            {
                return toDownload;
            }

            foreach (var entry in files)
            {
                var relativePath = entry["path"]?.ToString();
                var expectedHash = entry["hash"]?.ToString();
                if (string.IsNullOrEmpty(relativePath) || string.IsNullOrEmpty(expectedHash))
                {
                    continue;
                }

                if (!UpdatePathSecurity.TryResolvePath(installDir, relativePath, out var localPath))
                {
                    LauncherLog.Write($"Update: unsafe manifest path skipped: {relativePath}");
                    continue;
                }

                if (File.Exists(localPath) &&
                    ComputeSha256(localPath).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var packageName = entry["package"]?.ToString();
                if (string.IsNullOrEmpty(packageName))
                {
                    packageName = relativePath.Replace('\\', '/').Replace('/', '.');
                }

                toDownload.Add(new UpdateFileEntry
                {
                    RelativePath = relativePath,
                    ExpectedHash = expectedHash,
                    PackageName = packageName,
                });
            }

            return toDownload;
        }

        private static async Task<JObject?> DownloadManifestForVersionAsync(string version)
        {
            return await DownloadVerifiedManifestAsync(
                UpdateConfig.ManifestUrlsForVersion(version),
                UpdateConfig.ManifestSignatureUrlsForVersion(version),
                "Bridge manifest");
        }

        internal static async Task DownloadUpdateAsync(
            UpdateOffer offer,
            string installDir,
            IProgress<DownloadProgress>? progress,
            CancellationToken cancellationToken)
        {
            stagedVersion = offer.RemoteVersion;
            stagedInstallDir = installDir;

            var tempDir = GetStagingDirectory(offer.RemoteVersion);
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }

            Directory.CreateDirectory(tempDir);

            if (offer.IsZipUpdate && offer.ZipPackage != null)
            {
                var zipPackage = offer.ZipPackage;
                progress?.Report(new DownloadProgress
                {
                    CompletedFiles = 0,
                    TotalFiles = 1,
                    CurrentFile = zipPackage.Name,
                });

                var zipPath = Path.Combine(tempDir, zipPackage.Name);
                LauncherLog.Write($"Download package: {zipPackage.Name}");
                await DownloadAndVerifyFileAsync(
                    UpdateConfig.FileDownloadUrls(offer.RemoteVersion, zipPackage.Name),
                    zipPath,
                    zipPackage.Hash,
                    zipPackage.Name,
                    cancellationToken);
                UpdateZipPackage.ExtractToDirectory(zipPath, tempDir);

                try
                {
                    File.Delete(zipPath);
                }
                catch
                {
                    // ignore cleanup errors
                }

                UpdateFileHashesCatalog.SaveFromManifest(tempDir, offer.Manifest);
                var stagingManifest = Path.Combine(tempDir, "_staging.json");
                await File.WriteAllTextAsync(stagingManifest, offer.Manifest.ToString(), cancellationToken);
                UpdateStateHelper.Save(tempDir, offer.RemotePublished, offer.RemoteVersion, zipPackage.Hash);

                progress?.Report(new DownloadProgress
                {
                    CompletedFiles = 1,
                    TotalFiles = 1,
                    CurrentFile = string.Empty,
                });
                return;
            }

            var total = offer.FilesToDownload.Count;
            var completed = 0;
            foreach (var file in offer.FilesToDownload)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new DownloadProgress
                {
                    CompletedFiles = completed,
                    TotalFiles = total,
                    CurrentFile = file.RelativePath,
                });

                if (!UpdatePathSecurity.TryResolvePath(tempDir, file.RelativePath, out var destPath))
                {
                    throw new InvalidOperationException($"Unsafe update path: {file.RelativePath}");
                }

                var destDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }

                LauncherLog.Write($"Download: {file.PackageName}");
                await DownloadAndVerifyFileAsync(
                    UpdateConfig.FileDownloadUrls(offer.RemoteVersion, file.PackageName),
                    destPath,
                    file.ExpectedHash,
                    file.RelativePath,
                    cancellationToken);
                completed++;
            }

            UpdateFileHashesCatalog.SaveFromManifest(tempDir, offer.Manifest);
            var legacyStagingManifest = Path.Combine(tempDir, "_staging.json");
            await File.WriteAllTextAsync(legacyStagingManifest, offer.Manifest.ToString(), cancellationToken);
            UpdateStateHelper.Save(tempDir, offer.RemotePublished, offer.RemoteVersion);

            progress?.Report(new DownloadProgress
            {
                CompletedFiles = total,
                TotalFiles = total,
                CurrentFile = string.Empty,
            });
        }

        internal static bool HasStagedUpdate =>
            !string.IsNullOrEmpty(stagedVersion) && !string.IsNullOrEmpty(stagedInstallDir);

        internal static void ApplyUpdateAndRestart()
        {
            if (!HasStagedUpdate)
            {
                throw new InvalidOperationException(
                    LauncherLocalization.L(
                        "No downloaded update is ready to install.",
                        "Kein heruntergeladenes Update zum Installieren bereit."));
            }

            var tempDir = GetStagingDirectory(stagedVersion!);
            var launcherPath = Process.GetCurrentProcess().MainModule?.FileName
                ?? Path.Combine(stagedInstallDir!, "GameHelper.exe");
            var launcherName = Path.GetFileNameWithoutExtension(launcherPath);
            var pid = Environment.ProcessId;
            var updateRoot = Path.Combine(Path.GetTempPath(), "GameHelperUpdate");
            var scriptPath = Path.Combine(Path.GetTempPath(), $"GameHelperUpdate-install-{pid}.bat");

            var removeDirs = ReadRemoveFolders(tempDir, stagedInstallDir!);
            var removeLines = removeDirs
                .Select(dir => $"if exist {QuoteBatchPath(dir)} rd /s /q {QuoteBatchPath(dir)}")
                .ToList();

            var scriptLines = new List<string>
            {
                "@echo off",
                ":wait",
                $"tasklist /FI \"PID eq {pid}\" 2>NUL | find \"{pid}\" >NUL",
                "if %ERRORLEVEL%==0 (timeout /t 1 /nobreak >nul & goto wait)",
                "timeout /t 1 /nobreak >nul",
            };
            scriptLines.AddRange(removeLines);
            scriptLines.Add($"robocopy {QuoteBatchPath(tempDir)} {QuoteBatchPath(stagedInstallDir!)} /E /COPY:DAT /R:5 /W:2 /NFL /NDL /NJH /NJS /NC /NS /NP /XF _staging.json");
            scriptLines.Add($"start \"\" /D {QuoteBatchPath(stagedInstallDir!)} {QuoteBatchPath(launcherPath)}");
            scriptLines.Add($"rd /s /q {QuoteBatchPath(updateRoot)}");
            scriptLines.Add("del \"%~f0\"");

            var script = string.Join(Environment.NewLine, scriptLines);
            File.WriteAllText(scriptPath, script);

            var started = Process.Start(new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
                Arguments = $"/c {QuoteBatchPath(scriptPath)}",
                WorkingDirectory = Path.GetTempPath(),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });

            if (started == null)
            {
                throw new InvalidOperationException(
                    LauncherLocalization.L(
                        "Could not start the update installer.",
                        "Update-Installer konnte nicht gestartet werden."));
            }

            LauncherLog.Write($"Update install gestartet (PID {started.Id}) fuer {launcherName}");
        }

        private static IEnumerable<string> ReadRemoveFolders(string tempDir, string installDir)
        {
            var stagingJson = Path.Combine(tempDir, "_staging.json");
            if (!File.Exists(stagingJson))
            {
                yield break;
            }

            JArray? remove;
            try
            {
                var manifest = JObject.Parse(File.ReadAllText(stagingJson));
                remove = manifest["remove"] as JArray;
            }
            catch
            {
                yield break;
            }

            if (remove == null)
            {
                yield break;
            }

            var pluginsRoot = Path.Combine(installDir, "Plugins");
            foreach (var token in remove)
            {
                var rel = token?.ToString()?.Trim().Replace('/', Path.DirectorySeparatorChar);
                if (string.IsNullOrWhiteSpace(rel))
                {
                    continue;
                }

                if (!UpdatePathSecurity.TryResolvePath(installDir, rel, out var fullPath))
                {
                    LauncherLog.Write($"Update remove: unsafe path skipped: {rel}");
                    continue;
                }

                // Only allow direct subdirectories of Plugins\
                if (!fullPath.StartsWith(pluginsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    LauncherLog.Write($"Update remove: non-Plugins path rejected: {rel}");
                    continue;
                }

                var nameOnly = fullPath.Substring(pluginsRoot.Length + 1);
                if (nameOnly.Contains(Path.DirectorySeparatorChar))
                {
                    LauncherLog.Write($"Update remove: nested path rejected: {rel}");
                    continue;
                }

                yield return fullPath;
            }
        }

        private static string GetStagingDirectory(string version) =>
            Path.Combine(Path.GetTempPath(), "GameHelperUpdate", version);

        private static string QuoteBatchPath(string path) =>
            $"\"{path.Replace("\"", "\"\"")}\"";

        private static async Task<JObject?> DownloadManifestAsync()
        {
            return await DownloadVerifiedManifestAsync(
                UpdateConfig.ManifestUrls,
                UpdateConfig.ManifestSignatureUrls,
                "Manifest");
        }

        private static async Task<JObject?> DownloadVerifiedManifestAsync(
            IReadOnlyList<string> manifestUrls,
            IReadOnlyList<string> signatureUrls,
            string label)
        {
            var errors = new List<string>();
            for (var index = 0; index < manifestUrls.Count; index++)
            {
                var manifestUrl = manifestUrls[index];
                var signatureUrl = signatureUrls[Math.Min(index, signatureUrls.Count - 1)];
                try
                {
                    var content = await DownloadTextAsync(manifestUrl);
                    var signature = await DownloadTextAsync(signatureUrl);
                    if (!UpdateManifestVerifier.TryVerify(content, signature, out var verifyError))
                    {
                        throw new InvalidOperationException(verifyError);
                    }

                    LauncherLog.Write($"{label}: using {UpdateProxyOptions.DescribeSource(manifestUrl)}");
                    return JObject.Parse(content);
                }
                catch (Exception ex)
                {
                    errors.Add($"{UpdateProxyOptions.DescribeSource(manifestUrl)}: {ex.Message}");
                }
            }

            LauncherLog.Write($"{label} download failed: {string.Join("; ", errors)}");
            return null;
        }

        private static async Task<string> DownloadTextAsync(string url)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await HttpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            return await response.Content.ReadAsStringAsync();
        }

        private static async Task DownloadAndVerifyFileAsync(
            IReadOnlyList<string> urls,
            string destinationPath,
            string expectedHash,
            string label,
            CancellationToken cancellationToken)
        {
            var errors = new List<string>();
            foreach (var url in urls)
            {
                var source = UpdateProxyOptions.DescribeSource(url);
                try
                {
                    LauncherLog.Write($"Download source: {source}");
                    await DownloadFileAsync(url, destinationPath, cancellationToken);
                    VerifyDownloadedHash(destinationPath, expectedHash, label);
                    return;
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        File.Delete(destinationPath);
                    }
                    catch
                    {
                        // ignore cleanup errors
                    }

                    errors.Add($"{source}: {ex.Message}");
                }
            }

            throw new InvalidOperationException($"{label}: {string.Join("; ", errors)}");
        }

        private static void VerifyDownloadedHash(string filePath, string expectedHash, string relativePath)
        {
            var actualHash = ComputeSha256(filePath);
            if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    File.Delete(filePath);
                }
                catch
                {
                    // ignore cleanup errors
                }

                throw new InvalidOperationException(
                    $"Hash mismatch for {relativePath} (expected {expectedHash}, got {actualHash}).");
            }
        }

        internal static string GetCurrentVersion(string exePath)
        {
            try
            {
                var versionInfo = FileVersionInfo.GetVersionInfo(exePath);
                var version = versionInfo.FileVersion;
                if (string.IsNullOrEmpty(version))
                {
                    return "0.0.0";
                }

                var parts = version.Split('.');
                return parts.Length >= 3 ? $"{parts[0]}.{parts[1]}.{parts[2]}" : version.TrimEnd('.', '0');
            }
            catch
            {
                return "0.0.0";
            }
        }

        private static bool IsNewerVersion(string latestVersion, string currentVersion)
        {
            try
            {
                var latest = latestVersion.TrimStart('v').Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
                var current = currentVersion.TrimStart('v').Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
                var maxLen = Math.Max(latest.Length, current.Length);
                for (var i = 0; i < maxLen; i++)
                {
                    var l = i < latest.Length ? latest[i] : 0;
                    var c = i < current.Length ? current[i] : 0;
                    if (l > c)
                    {
                        return true;
                    }

                    if (l < c)
                    {
                        return false;
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private static async Task DownloadFileAsync(string url, string destinationPath, CancellationToken cancellationToken)
        {
            var fileLabel = Path.GetFileName(destinationPath);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"HTTP {(int)response.StatusCode} {response.ReasonPhrase} - {fileLabel}");
            }

            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var file = File.Create(destinationPath);
                await stream.CopyToAsync(file, cancellationToken);
            }
            catch (Exception ex) when (ex is not HttpRequestException)
            {
                throw new IOException($"Could not write {fileLabel}: {ex.Message}", ex);
            }
        }

        private static string ComputeSha256(string filePath)
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            return Convert.ToHexString(sha.ComputeHash(stream));
        }

        private static IReadOnlyList<string> ParseChangelog(JObject manifest)
        {
            var token = manifest["changelog"];
            if (token == null)
            {
                return DefaultChangelog(manifest["version"]?.ToString());
            }

            if (token is JArray array)
            {
                var lines = array
                    .Select(x => x?.ToString()?.Trim())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x!)
                    .ToList();
                return lines.Count > 0 ? lines : DefaultChangelog(manifest["version"]?.ToString());
            }

            var text = token.ToString();
            var split = text
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .ToList();
            return split.Count > 0 ? split : DefaultChangelog(manifest["version"]?.ToString());
        }

        private static IReadOnlyList<string> DefaultChangelog(string? version)
        {
            var label = string.IsNullOrWhiteSpace(version) ? "This release" : $"Version {version}";
            return new[]
            {
                $"{label} includes improvements and bug fixes. || {label} enthaelt Verbesserungen und Fehlerbehebungen.",
                "Plugins and settings were updated. || Plugins und Einstellungen wurden aktualisiert.",
            };
        }
    }
}
