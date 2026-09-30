namespace GitHubAutoInstaller.Models;

public sealed record UpdateInfo(
    bool IsUpdateAvailable,
    string CurrentVersion,
    string LatestVersion,
    string? ReleaseNotes,
    Uri? ReleasePageUrl,
    ReleaseAsset? InstallerAsset,
    ReleaseAsset? StandaloneAsset,
    ReleaseAsset? ChecksumAsset,
    DateTimeOffset? PublishedAt)
{
    public static UpdateInfo None(string currentVersion) =>
        new(
            IsUpdateAvailable: false,
            CurrentVersion: currentVersion,
            LatestVersion: currentVersion,
            ReleaseNotes: null,
            ReleasePageUrl: null,
            InstallerAsset: null,
            StandaloneAsset: null,
            ChecksumAsset: null,
            PublishedAt: null);
}
