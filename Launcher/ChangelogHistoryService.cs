namespace Launcher
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Net.Http;
    using System.Threading.Tasks;
    using Newtonsoft.Json.Linq;

    internal sealed class ReleaseHistoryEntry
    {
        public string Version { get; init; } = string.Empty;
        public string Published { get; init; } = string.Empty;
        public IReadOnlyList<string> Changelog { get; init; } = Array.Empty<string>();
    }

    internal static class ChangelogHistoryService
    {
        internal static async Task<IReadOnlyList<ReleaseHistoryEntry>> LoadMergedAsync(string installDir)
        {
            var byVersion = new Dictionary<string, ReleaseHistoryEntry>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in ParseFile(Path.Combine(installDir, "changelog-history.json")))
                MergeEntry(byVersion, entry);

            try
            {
                foreach (var entry in await DownloadRemoteAsync())
                    MergeEntry(byVersion, entry);
            }
            catch (Exception ex)
            {
                LauncherLog.Write($"Changelog-History remote: {ex.Message}");
            }

            return byVersion.Values
                .OrderByDescending(e => ParseVersion(e.Version))
                .ThenByDescending(e => e.Published, StringComparer.Ordinal)
                .ToList();
        }

        internal static string FormatForDisplay(IReadOnlyList<ReleaseHistoryEntry> releases)
        {
            if (releases.Count == 0)
            {
                return LauncherLocalization.L(
                    "No release history available yet.",
                    "Noch keine Release-Historie vorhanden.");
            }

            var blocks = new List<string>();
            foreach (var release in releases)
            {
                var date = FormatPublishedDate(release.Published);
                var header = string.IsNullOrEmpty(date)
                    ? $"v{NormalizeVersion(release.Version)}"
                    : $"v{NormalizeVersion(release.Version)}  ({date})";
                blocks.Add(header);

                var lines = ChangelogLocalization.ResolveLines(release.Changelog);
                if (lines.Count == 0)
                {
                    blocks.Add(LauncherLocalization.L(
                        "  • Improvements and bug fixes.",
                        "  • Verbesserungen und Fehlerbehebungen."));
                }
                else
                {
                    foreach (var line in lines)
                        blocks.Add($"  • {line}");
                }

                blocks.Add(string.Empty);
            }

            return string.Join(Environment.NewLine, blocks).TrimEnd();
        }

        private static void MergeEntry(
            Dictionary<string, ReleaseHistoryEntry> byVersion,
            ReleaseHistoryEntry entry)
        {
            if (string.IsNullOrWhiteSpace(entry.Version))
                return;

            var key = NormalizeVersion(entry.Version);
            if (!byVersion.TryGetValue(key, out var existing))
            {
                byVersion[key] = entry;
                return;
            }

            if (string.Compare(entry.Published, existing.Published, StringComparison.Ordinal) > 0)
                byVersion[key] = entry;
        }

        private static IReadOnlyList<ReleaseHistoryEntry> ParseFile(string path)
        {
            if (!File.Exists(path))
                return Array.Empty<ReleaseHistoryEntry>();

            try
            {
                var root = JObject.Parse(File.ReadAllText(path));
                return ParseReleasesArray(root["releases"] as JArray);
            }
            catch (Exception ex)
            {
                LauncherLog.Write($"Changelog-History local ({path}): {ex.Message}");
                return Array.Empty<ReleaseHistoryEntry>();
            }
        }

        private static async Task<IReadOnlyList<ReleaseHistoryEntry>> DownloadRemoteAsync()
        {
            foreach (var url in UpdateConfig.ChangelogHistoryUrls)
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    using var response = await UpdateService.SharedHttpClient.SendAsync(request).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                        continue;

                    var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var root = JObject.Parse(json);
                    return ParseReleasesArray(root["releases"] as JArray);
                }
                catch (Exception ex)
                {
                    LauncherLog.Write(
                        $"Changelog-History remote ({UpdateProxyOptions.DescribeSource(url)}): {ex.Message}");
                }
            }

            return Array.Empty<ReleaseHistoryEntry>();
        }

        private static IReadOnlyList<ReleaseHistoryEntry> ParseReleasesArray(JArray? array)
        {
            if (array == null || array.Count == 0)
                return Array.Empty<ReleaseHistoryEntry>();

            var list = new List<ReleaseHistoryEntry>();
            foreach (var token in array)
            {
                if (token is not JObject obj)
                    continue;

                var version = obj["version"]?.ToString()?.Trim();
                if (string.IsNullOrEmpty(version))
                    continue;

                var changelog = new List<string>();
                if (obj["changelog"] is JArray lines)
                {
                    foreach (var line in lines)
                    {
                        var text = line?.ToString()?.Trim();
                        if (!string.IsNullOrEmpty(text))
                            changelog.Add(text);
                    }
                }

                list.Add(new ReleaseHistoryEntry
                {
                    Version = version,
                    Published = obj["published"]?.ToString() ?? string.Empty,
                    Changelog = changelog,
                });
            }

            return list;
        }

        private static Version ParseVersion(string version)
        {
            var normalized = NormalizeVersion(version);
            return Version.TryParse(normalized, out var parsed) ? parsed : new Version(0, 0, 0);
        }

        private static string NormalizeVersion(string version) =>
            version.Trim().TrimStart('v', 'V');

        private static string FormatPublishedDate(string published)
        {
            if (string.IsNullOrWhiteSpace(published))
                return string.Empty;

            if (!DateTime.TryParse(published, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
                return string.Empty;

            return dt.ToLocalTime().ToString("dd.MM.yyyy", CultureInfo.CurrentCulture);
        }
    }
}
