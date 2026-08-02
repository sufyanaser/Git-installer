namespace GitHubAutoInstaller.Models;

public sealed record GitHubRelease(
    string TagName,
    Uri PageUrl,
    IReadOnlyList<ReleaseAsset> Assets);
