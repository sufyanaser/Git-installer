namespace GitHubAutoInstaller.Models;

public sealed record RepositoryContentItem(
    string Name,
    string Path,
    string Type,
    long Size,
    Uri? DownloadUrl);
