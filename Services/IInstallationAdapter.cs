using GitHubAutoInstaller.Models;

namespace GitHubAutoInstaller.Services;

/// <summary>
/// Defines an independent, testable installation engine adapter.
/// </summary>
public interface IInstallationAdapter
{
    InstallationMethod Method { get; }

    bool CanHandle(InstallationPlan plan);

    Task<InstallationResult> ExecuteAsync(
        InstallationPlan plan,
        string? downloadedAssetPath,
        bool silent,
        Action<string> log,
        CancellationToken cancellationToken);
}
