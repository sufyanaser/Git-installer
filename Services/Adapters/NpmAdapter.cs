using System.Text.RegularExpressions;
using GitHubAutoInstaller.Models;

namespace GitHubAutoInstaller.Services.Adapters;

/// <summary>
/// Executes Node.js global CLI installations using npm with argument validation.
/// </summary>
public sealed class NpmAdapter : IInstallationAdapter
{
    private readonly ToolVerificationService _toolVerifier;

    public NpmAdapter(ToolVerificationService? toolVerifier = null)
    {
        _toolVerifier = toolVerifier ?? new ToolVerificationService();
    }

    public InstallationMethod Method => InstallationMethod.NpmPackage;

    public bool CanHandle(InstallationPlan plan) => plan.Method == InstallationMethod.NpmPackage;

    public async Task<InstallationResult> ExecuteAsync(
        InstallationPlan plan,
        string? downloadedAssetPath,
        bool silent,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        ToolRequirement npm = await _toolVerifier.CheckToolAsync("npm", cancellationToken: cancellationToken);
        if (!npm.IsInstalled)
        {
            throw new InvalidOperationException($"npm is not available: {npm.ResolutionGuidance}");
        }

        string packageName = plan.Repository.Name.ToLowerInvariant();
        if (!Regex.IsMatch(packageName, @"^[a-z0-9_\-\.]+$") || packageName.StartsWith('-'))
        {
            throw new InvalidOperationException($"Invalid npm package name '{packageName}'.");
        }

        List<string> args = ["install", "-g", packageName];
        if (silent)
        {
            args.Add("--silent");
        }

        log($"Executing npm: npm {string.Join(" ", args)}");

        int exitCode = await ProcessExecutionService.RunAsync(
            "npm.cmd",
            args,
            null,
            log,
            cancellationToken);

        return new InstallationResult(null, exitCode);
    }
}
