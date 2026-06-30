namespace Launcher
{
    using System.Collections.Generic;
    using Newtonsoft.Json.Linq;
    using Shared.UpdateSecurity;

    internal sealed class UpdateCheckResult
    {
        public UpdateOffer? Offer { get; init; }

        public UpdateMigrationNotice.Info? MigrationNotice { get; init; }

        public string? ErrorMessage { get; init; }
    }

    internal sealed class UpdateOffer
    {
        public required string CurrentVersion { get; init; }

        public required string RemoteVersion { get; init; }

        public required string RemotePublished { get; init; }

        public required JObject Manifest { get; init; }

        public required IReadOnlyList<UpdateFileEntry> FilesToDownload { get; init; }

        public UpdateZipPackage.PackageInfo? ZipPackage { get; init; }

        public UpdateMigrationNotice.Info? MigrationNotice { get; init; }

        public IReadOnlyList<string> Changelog { get; init; } = new List<string>();

        public bool IsZipUpdate => this.ZipPackage != null;

        public int FileCount => this.IsZipUpdate ? 1 : this.FilesToDownload.Count;
    }

    internal sealed class UpdateFileEntry
    {
        public required string RelativePath { get; init; }

        public required string ExpectedHash { get; init; }

        public required string PackageName { get; init; }
    }

    internal sealed class DownloadProgress
    {
        public int CompletedFiles { get; init; }

        public int TotalFiles { get; init; }

        public string CurrentFile { get; init; } = string.Empty;

        public int Percent => this.TotalFiles <= 0 ? 0 : (int)(100f * this.CompletedFiles / this.TotalFiles);
    }
}
