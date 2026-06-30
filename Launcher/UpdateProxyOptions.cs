namespace Launcher
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using Newtonsoft.Json.Linq;

    internal enum UpdateProxyMode
    {
        GitHubProxy,
        NetworkProxy,
        Direct,
    }

    internal sealed class UpdateProxyOptions
    {
        private const string ConfigFileName = "update.proxy.json";
        private const string GitHubProxyEnvironmentVariable = "GAMEHELPER_GITHUB_PROXY";
        private const string NetworkProxyEnvironmentVariable = "GAMEHELPER_NETWORK_PROXY";
        private const string DirectEnvironmentVariable = "GAMEHELPER_UPDATE_DIRECT";

        private static readonly string[] DefaultGitHubProxyBaseUrls =
        [
            "https://gh-proxy.com/",
            "https://gh-proxy.net/",
        ];

        internal UpdateProxyMode Mode { get; init; } = UpdateProxyMode.Direct;

        internal string GitHubProxyBaseUrls { get; init; } = string.Join(";", DefaultGitHubProxyBaseUrls);

        internal string NetworkProxyUrl { get; init; } = string.Empty;

        internal bool UsesGitHubProxy => this.Mode == UpdateProxyMode.GitHubProxy;

        internal bool UsesNetworkProxy => this.Mode == UpdateProxyMode.NetworkProxy;

        internal static UpdateProxyOptions Load(string installDir)
        {
            var loaded = LoadFromFile(installDir) ?? new UpdateProxyOptions();
            if (IsTruthy(Environment.GetEnvironmentVariable(DirectEnvironmentVariable)))
            {
                return loaded.WithMode(UpdateProxyMode.Direct);
            }

            var networkProxy = Environment.GetEnvironmentVariable(NetworkProxyEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(networkProxy))
            {
                return new UpdateProxyOptions
                {
                    Mode = UpdateProxyMode.NetworkProxy,
                    NetworkProxyUrl = networkProxy.Trim(),
                    GitHubProxyBaseUrls = loaded.GitHubProxyBaseUrls,
                };
            }

            var githubProxy = Environment.GetEnvironmentVariable(GitHubProxyEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(githubProxy))
            {
                return new UpdateProxyOptions
                {
                    Mode = UpdateProxyMode.GitHubProxy,
                    GitHubProxyBaseUrls = githubProxy.Trim(),
                    NetworkProxyUrl = loaded.NetworkProxyUrl,
                };
            }

            return loaded;
        }

        internal void Save(string installDir)
        {
            var path = ConfigPath(installDir);
            var root = new JObject
            {
                ["mode"] = this.Mode.ToString(),
                ["githubProxy"] = this.GitHubProxyBaseUrls,
                ["networkProxy"] = this.NetworkProxyUrl,
            };

            File.WriteAllText(path, root.ToString());
        }

        internal IReadOnlyList<string> BuildCandidateUrls(string githubUrl)
        {
            if (this.Mode == UpdateProxyMode.Direct)
            {
                return [githubUrl];
            }

            if (this.Mode == UpdateProxyMode.NetworkProxy)
            {
                return [githubUrl];
            }

            var urls = new List<string>();
            foreach (var proxyBaseUrl in SplitList(this.GitHubProxyBaseUrls))
            {
                urls.Add(ToGitHubProxyUrl(proxyBaseUrl, githubUrl));
            }

            urls.Add(githubUrl);
            return urls.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        internal Uri? GetNetworkProxyUri()
        {
            if (!this.UsesNetworkProxy || string.IsNullOrWhiteSpace(this.NetworkProxyUrl))
            {
                return null;
            }

            var raw = this.NetworkProxyUrl.Trim();
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
                string.IsNullOrWhiteSpace(uri.Host))
            {
                throw new InvalidOperationException($"Invalid network proxy URL: {raw}");
            }

            if (!IsSupportedNetworkProxyScheme(uri.Scheme))
            {
                throw new InvalidOperationException(
                    $"Unsupported network proxy scheme: {uri.Scheme}. Use http, https, socks4, socks4a, or socks5.");
            }

            return uri;
        }

        internal string DisplayName()
        {
            return this.Mode switch
            {
                UpdateProxyMode.Direct => LauncherLocalization.L("Direct GitHub", "GitHub direkt"),
                UpdateProxyMode.NetworkProxy => LauncherLocalization.L("HTTP / SOCKS proxy", "HTTP- / SOCKS-Proxy"),
                _ => LauncherLocalization.L("GitHub URL proxy", "GitHub-URL-Proxy"),
            };
        }

        internal static string DescribeSource(string url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out var uri)
                ? uri.Host
                : url;
        }

        private static UpdateProxyOptions? LoadFromFile(string installDir)
        {
            var path = ConfigPath(installDir);
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                var root = JObject.Parse(File.ReadAllText(path));
                var modeText = root["mode"]?.ToString();
                var mode = Enum.TryParse<UpdateProxyMode>(modeText, ignoreCase: true, out var parsed)
                    ? parsed
                    : UpdateProxyMode.GitHubProxy;

                return new UpdateProxyOptions
                {
                    Mode = mode,
                    GitHubProxyBaseUrls = root["githubProxy"]?.ToString() ?? string.Join(";", DefaultGitHubProxyBaseUrls),
                    NetworkProxyUrl = root["networkProxy"]?.ToString() ?? string.Empty,
                };
            }
            catch (Exception ex)
            {
                LauncherLog.Write($"Proxy config ignored: {ex.Message}");
                return null;
            }
        }

        private static string ConfigPath(string installDir) =>
            Path.Combine(installDir, ConfigFileName);

        private static IEnumerable<string> SplitList(string value)
        {
            return value
                .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static x => !string.IsNullOrWhiteSpace(x));
        }

        private static string ToGitHubProxyUrl(string proxyBaseUrl, string githubUrl)
        {
            var proxy = proxyBaseUrl.Trim();
            if (proxy.Contains("{escapedUrl}", StringComparison.OrdinalIgnoreCase))
            {
                return proxy.Replace("{escapedUrl}", Uri.EscapeDataString(githubUrl), StringComparison.OrdinalIgnoreCase);
            }

            if (proxy.Contains("{url}", StringComparison.OrdinalIgnoreCase))
            {
                return proxy.Replace("{url}", githubUrl, StringComparison.OrdinalIgnoreCase);
            }

            return proxy.EndsWith("/", StringComparison.Ordinal)
                ? $"{proxy}{githubUrl}"
                : $"{proxy}/{githubUrl}";
        }

        private static bool IsSupportedNetworkProxyScheme(string scheme) =>
            scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
            scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ||
            scheme.Equals("socks4", StringComparison.OrdinalIgnoreCase) ||
            scheme.Equals("socks4a", StringComparison.OrdinalIgnoreCase) ||
            scheme.Equals("socks5", StringComparison.OrdinalIgnoreCase);

        private static bool IsTruthy(string? value) =>
            value != null &&
            (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
             value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
             value.Equals("yes", StringComparison.OrdinalIgnoreCase));

        private UpdateProxyOptions WithMode(UpdateProxyMode mode) =>
            new()
            {
                Mode = mode,
                GitHubProxyBaseUrls = this.GitHubProxyBaseUrls,
                NetworkProxyUrl = this.NetworkProxyUrl,
            };
    }
}
