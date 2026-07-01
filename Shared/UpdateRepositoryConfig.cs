namespace Shared.UpdateSecurity
{
    using System;
    using System.IO;
    using Newtonsoft.Json.Linq;

    /// <summary>
    ///     Resolves GitHub repository from optional github.config.json next to the executable.
    /// </summary>
    public static class UpdateRepositoryConfig
    {
        public const string DefaultRepository = "Pililink/Gamehelper";
        public const string GitHubHost = "https://github.com";
        public const string ManifestFileName = "manifest.json";
        public const string ManifestSignatureFileName = "manifest.sig";
        public const string FileHashesFileName = UpdateFileHashesCatalog.CatalogFileName;

        private static readonly Lazy<string> RepositoryLazy = new(ResolveRepository);

        public static string Repository => RepositoryLazy.Value;

        public static string ManifestUrl =>
            $"{GitHubHost}/{Repository}/releases/latest/download/{ManifestFileName}";

        public static string ManifestSignatureUrl =>
            $"{GitHubHost}/{Repository}/releases/latest/download/{ManifestSignatureFileName}";

        public static string FileDownloadUrl(string version, string packageFileName)
        {
            var tag = version.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? version : $"v{version}";
            return $"{GitHubHost}/{Repository}/releases/download/{tag}/{packageFileName}";
        }

        private static string ResolveRepository()
        {
            try
            {
                var configPath = Path.Combine(AppContext.BaseDirectory, "github.config.json");
                if (!File.Exists(configPath))
                {
                    return DefaultRepository;
                }

                var json = JObject.Parse(File.ReadAllText(configPath));
                var repo = json["repository"]?.ToString()?.Trim();
                if (string.IsNullOrWhiteSpace(repo) || !IsValidRepository(repo))
                {
                    return DefaultRepository;
                }

                return repo;
            }
            catch
            {
                return DefaultRepository;
            }
        }

        private static bool IsValidRepository(string repo)
        {
            var parts = repo.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 2)
            {
                return false;
            }

            foreach (var part in parts)
            {
                if (part.Length == 0 || part.Contains(' ', StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
