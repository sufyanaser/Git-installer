namespace GitHubAutoInstaller.Models;

public sealed record GitHubRepository(string Owner, string Name)
{
    public string FullName => $"{Owner}/{Name}";
}
