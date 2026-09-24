namespace GitHubAutoInstaller.Models;

public sealed record RepositoryClassification(
    RepositoryCategory Category,
    string CategoryDescription,
    IReadOnlyList<RepositoryEcosystem> DetectedEcosystems,
    bool IsWindowsCompatible,
    string WindowsCompatibilityDetails,
    IReadOnlyList<string> RequiredExternalTools,
    bool CanInstallAutomatically,
    string? UnsupportedReason);
