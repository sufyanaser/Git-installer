namespace GitHubAutoInstaller.Models;

public sealed record InstallationResult(string? InstalledDirectory, int ExitCode);
