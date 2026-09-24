using GitHubAutoInstaller.Models;

namespace GitHubAutoInstaller.Services.Adapters;

/// <summary>
/// Executes Rust application installations using Cargo.
/// </summary>
public sealed class CargoAdapter : IInstallationAdapter
{
    private readonly ToolVerificationService _toolVerifier;

    public CargoAdapter(ToolVerificationService? toolVerifier = null)
    {
        _toolVerifier = toolVerifier ?? new ToolVerificationService();
    }

    public InstallationMethod Method => InstallationMethod.CargoCrate;

    public bool CanHandle(InstallationPlan plan) => plan.Method == InstallationMethod.CargoCrate;

    public async Task<InstallationResult> ExecuteAsync(
        InstallationPlan plan,
        string? downloadedAssetPath,
        bool silent,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        ToolRequirement cargo = await _toolVerifier.CheckToolAsync("cargo", cancellationToken: cancellationToken);
        if (!cargo.IsInstalled)
        {
            throw new InvalidOperationException($"Cargo (Rust toolchain) is not available: {cargo.ResolutionGuidance}");
        }

        string repoUrl = $"https://github.com/{plan.Repository.FullName}.git";
        List<string> args = ["install", "--git", repoUrl, "--locked"];
        if (silent)
        {
            args.Add("--quiet");
        }

        log($"Executing Cargo: cargo {string.Join(" ", args)}");

        int exitCode = await ProcessExecutionService.RunAsync(
            "cargo.exe",
            args,
            null,
            log,
            cancellationToken);

        return new InstallationResult(null, exitCode);
    }
}
