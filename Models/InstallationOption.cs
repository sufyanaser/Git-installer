namespace GitHubAutoInstaller.Models;

/// <summary>
/// A selectable installation option presented when multiple installation methods are available.
/// </summary>
public sealed record InstallationOption(
    string Key,
    string Title,
    string Description,
    RepositoryEcosystem Ecosystem,
    InstallationMethod Method,
    bool IsRecommended,
    InstallationPlan Plan);
