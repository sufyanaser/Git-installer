namespace GitHubAutoInstaller.Models;

public sealed record RepositoryMetadata(
    string FullName,
    string Description,
    int Stars,
    Uri? AvatarUrl);
