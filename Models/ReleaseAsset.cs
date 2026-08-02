namespace GitHubAutoInstaller.Models;

public sealed record ReleaseAsset(
    string Name,
    Uri DownloadUrl,
    long Size,
    string ContentType);
