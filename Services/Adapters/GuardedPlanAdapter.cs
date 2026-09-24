using GitHubAutoInstaller.Models;

namespace GitHubAutoInstaller.Services.Adapters;

/// <summary>
/// Handles guarded methods (Docker setup plans and manual instructions) that must never be executed automatically.
/// </summary>
public sealed class GuardedPlanAdapter : IInstallationAdapter
{
    public InstallationMethod Method => InstallationMethod.DockerSetupPlan;

    public bool CanHandle(InstallationPlan plan) =>
        plan.Method is InstallationMethod.DockerSetupPlan or InstallationMethod.ManualInstructions;

    public Task<InstallationResult> ExecuteAsync(
        InstallationPlan plan,
        string? downloadedAssetPath,
        bool silent,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.Method == InstallationMethod.DockerSetupPlan)
        {
            throw new InvalidOperationException(
                "Docker-based projects receive a setup plan only. Automated launching of containers is guarded to prevent unexpected system modifications. Follow the displayed commands manually in your terminal.");
        }

        throw new InvalidOperationException(
            "This repository requires manual installation or external build steps. Refer to the repository documentation.");
    }
}
