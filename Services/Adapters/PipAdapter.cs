using System.Text.RegularExpressions;
using GitHubAutoInstaller.Models;

namespace GitHubAutoInstaller.Services.Adapters;

/// <summary>
/// Executes Python package installations using pip with strict argument validation.
/// </summary>
public sealed class PipAdapter : IInstallationAdapter
{
    private readonly ToolVerificationService _toolVerifier;

    public PipAdapter(ToolVerificationService? toolVerifier = null)
    {
        _toolVerifier = toolVerifier ?? new ToolVerificationService();
    }

    public InstallationMethod Method => InstallationMethod.PipPackage;

    public bool CanHandle(InstallationPlan plan) => plan.Method == InstallationMethod.PipPackage;

    public async Task<InstallationResult> ExecuteAsync(
        InstallationPlan plan,
        string? downloadedAssetPath,
        bool silent,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        ToolRequirement pip = await _toolVerifier.CheckToolAsync("pip", cancellationToken: cancellationToken);
        if (!pip.IsInstalled)
        {
            throw new InvalidOperationException($"pip is not available: {pip.ResolutionGuidance}");
        }

        string packageName = plan.Repository.Name;
        if (!Regex.IsMatch(packageName, @"^[a-zA-Z0-9_\-\.]+$") || packageName.StartsWith('-'))
        {
            throw new InvalidOperationException($"Invalid Python package name '{packageName}'.");
        }

        List<string> args = ["install", "--no-cache-dir", packageName];
        if (silent)
        {
            args.Add("--quiet");
        }

        log($"Executing pip: pip {string.Join(" ", args)}");

        int exitCode = await ProcessExecutionService.RunAsync(
            "pip.exe",
            args,
            null,
            log,
            cancellationToken);

        return new InstallationResult(null, exitCode);
    }
}
