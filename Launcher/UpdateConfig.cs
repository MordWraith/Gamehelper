namespace Launcher

{

    using System.Collections.Generic;
    using Shared.UpdateSecurity;



    internal static class UpdateConfig

    {

        internal const string ChangelogHistoryFileName = "changelog-history.json";



        internal static string ManifestUrl => UpdateRepositoryConfig.ManifestUrl;

        internal static IReadOnlyList<string> ManifestUrls =>
            UpdateService.ProxyOptions.BuildCandidateUrls(UpdateRepositoryConfig.ManifestUrl);



        internal static string ManifestSignatureUrl => UpdateRepositoryConfig.ManifestSignatureUrl;

        internal static IReadOnlyList<string> ManifestSignatureUrls =>
            UpdateService.ProxyOptions.BuildCandidateUrls(UpdateRepositoryConfig.ManifestSignatureUrl);



        internal static string ChangelogHistoryUrl =>

            $"{UpdateRepositoryConfig.GitHubHost}/{UpdateRepositoryConfig.Repository}/releases/latest/download/{ChangelogHistoryFileName}";

        internal static IReadOnlyList<string> ChangelogHistoryUrls =>
            UpdateService.ProxyOptions.BuildCandidateUrls(ChangelogHistoryUrl);



        internal static string FileDownloadUrl(string version, string fileName) =>

            UpdateRepositoryConfig.FileDownloadUrl(version, fileName);

        internal static IReadOnlyList<string> FileDownloadUrls(string version, string fileName) =>
            UpdateService.ProxyOptions.BuildCandidateUrls(UpdateRepositoryConfig.FileDownloadUrl(version, fileName));



        internal static string ManifestSignatureUrlForVersion(string version)

        {

            var tag = version.StartsWith('v') ? version : $"v{version}";

            return $"{UpdateRepositoryConfig.GitHubHost}/{UpdateRepositoryConfig.Repository}/releases/download/{tag}/{UpdateRepositoryConfig.ManifestSignatureFileName}";

        }

        internal static IReadOnlyList<string> ManifestUrlsForVersion(string version)
        {
            var tag = version.StartsWith('v') ? version : $"v{version}";
            return UpdateService.ProxyOptions.BuildCandidateUrls(
                $"{UpdateRepositoryConfig.GitHubHost}/{UpdateRepositoryConfig.Repository}/releases/download/{tag}/{UpdateRepositoryConfig.ManifestFileName}");
        }

        internal static IReadOnlyList<string> ManifestSignatureUrlsForVersion(string version) =>
            UpdateService.ProxyOptions.BuildCandidateUrls(ManifestSignatureUrlForVersion(version));

    }

}


