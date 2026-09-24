using System.Text.RegularExpressions;
using GitHubAutoInstaller.Models;

namespace GitHubAutoInstaller.Services.Adapters;

/// <summary>
/// Executes .NET global tool installations using the dotnet CLI.
/// </summary>
public sealed class DotNetToolAdapter : IInstallationAdapter
{
    private readonly ToolVerificationService _toolVerifier;

    public DotNetToolAdapter(ToolVerificationService? toolVerifier = null)
    {
        _toolVerifier = toolVerifier ?? new ToolVerificationService();
    }

    public InstallationMethod Method => InstallationMethod.DotNetTool;

    public bool CanHandle(InstallationPlan plan) => plan.Method == InstallationMethod.DotNetTool;

    public async Task<InstallationResult> ExecuteAsync(
        InstallationPlan plan,
        string? downloadedAssetPath,
        bool silent,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        ToolRequirement dotnet = await _toolVerifier.CheckToolAsync("dotnet", cancellationToken: cancellationToken);
        if (!dotnet.IsInstalled)
        {
            throw new InvalidOperationException($".NET SDK is not available: {dotnet.ResolutionGuidance}");
        }

        string toolName = plan.Repository.Name.ToLowerInvariant();
        if (!Regex.IsMatch(toolName, @"^[a-z0-9_\-\.]+$") || toolName.StartsWith('-'))
        {
            throw new InvalidOperationException($"Invalid .NET tool name '{toolName}'.");
        }

        List<string> args = ["tool", "install", "--global", toolName];

        log($"Executing .NET CLI: dotnet {string.Join(" ", args)}");

        int exitCode = await ProcessExecutionService.RunAsync(
            "dotnet.exe",
            args,
            null,
            log,
            cancellationToken);

        return new InstallationResult(null, exitCode);
    }
}
