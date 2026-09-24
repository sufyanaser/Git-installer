namespace GitHubAutoInstaller.Models;

public sealed record ProposedCommand(
    string Executable,
    IReadOnlyList<string> Arguments,
    string DisplayCommand,
    string Purpose);
