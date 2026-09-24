namespace GitHubAutoInstaller.Models;

public sealed record ToolRequirement(
    string ToolName,
    bool IsInstalled,
    string? FoundVersion,
    string? MinimumVersion,
    string ResolutionGuidance);
