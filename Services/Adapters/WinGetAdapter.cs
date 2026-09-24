using System.Text.RegularExpressions;
using GitHubAutoInstaller.Models;

namespace GitHubAutoInstaller.Services.Adapters;

/// <summary>
/// Executes installations using the official Windows Package Manager (WinGet).
/// </summary>
public sealed class WinGetAdapter : IInstallationAdapter
{
    private readonly ToolVerificationService _toolVerifier;

    public WinGetAdapter(ToolVerificationService? toolVerifier = null)
    {
        _toolVerifier = toolVerifier ?? new ToolVerificationService();
    }

    public InstallationMethod Method => InstallationMethod.WinGetPackage;

    public bool CanHandle(InstallationPlan plan) => plan.Method == InstallationMethod.WinGetPackage;

    public async Task<InstallationResult> ExecuteAsync(
        InstallationPlan plan,
        string? downloadedAssetPath,
        bool silent,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        ToolRequirement tool = await _toolVerifier.CheckToolAsync("winget", cancellationToken: cancellationToken);
        if (!tool.IsInstalled)
        {
            throw new InvalidOperationException($"WinGet is not available: {tool.ResolutionGuidance}");
        }

        string packageId = $"{plan.Repository.Owner}.{plan.Repository.Name}";

        // Validate package ID to prevent argument/command injection
        if (!Regex.IsMatch(packageId, @"^[a-zA-Z0-9_\-\.]+$") || packageId.StartsWith('-'))
        {
            throw new InvalidOperationException($"Invalid WinGet package identifier '{packageId}'.");
        }

        List<string> args = [
            "install",
            "--id", packageId,
            "--exact",
            "--accept-package-agreements",
            "--accept-source-agreements"
        ];

        if (silent)
        {
            args.Add("--silent");
        }

        log($"Executing WinGet: winget {string.Join(" ", args)}");

        int exitCode = await ProcessExecutionService.RunAsync(
            "winget.exe",
            args,
            null,
            log,
            cancellationToken);

        return new InstallationResult(null, exitCode);
    }
}
