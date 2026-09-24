using System.IO;
using GitHubAutoInstaller.Models;

namespace GitHubAutoInstaller.Services;

/// <summary>
/// Produces an inspectable installation plan without executing downloaded content.
/// A repository manifest is evidence of a build ecosystem, not permission to run its scripts.
/// </summary>
public static class InstallationPlanService
{
    public static InstallationPlan ForReleaseAsset(ReleaseAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        string extension = Path.GetExtension(asset.Name).ToLowerInvariant();
        return extension switch
        {
            ".exe" => new("Windows executable installer", "Download and launch the publisher's executable interactively.", true),
            ".msi" => new("Windows Installer package", "Download and launch the MSI using Windows Installer.", true),
            ".zip" => new("Portable ZIP archive", "Download, validate archive paths and extract to the application directory.", false),
            ".ps1" => new("PowerShell script", "Download and execute publisher-provided PowerShell code only after explicit approval.", true),
            _ => throw new InvalidOperationException("Unsupported release asset.")
        };
    }

    public static IReadOnlyList<RepositoryInstallOption> DetectFromRootFiles(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        HashSet<string> names = new(
            paths.Where(path => !string.IsNullOrWhiteSpace(path) && !path.Contains('/'))
                 .Select(path => path.ToLowerInvariant()),
            StringComparer.OrdinalIgnoreCase);
        List<RepositoryInstallOption> options = [];

        if (names.Contains("pyproject.toml") || names.Contains("setup.py") || names.Contains("requirements.txt"))
            options.Add(new("Python", "pip", "Python project detected. Review its documentation and dependencies before installing."));
        if (names.Contains("package.json"))
            options.Add(new("Node.js", "npm", "Node.js manifest detected. Review package scripts and the documented installation command."));
        if (names.Contains("cargo.toml"))
            options.Add(new("Rust", "cargo", "Rust manifest detected. A toolchain and a documented build or install target may be required."));
        if (names.Any(name => name.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                              name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
            options.Add(new(".NET", "dotnet", ".NET project detected. Inspect the target framework and application entry point."));
        if (names.Contains("winget-pkgs.yaml") || names.Contains("winget.yaml"))
            options.Add(new("WinGet manifest", "winget", "Review the package identifier and publisher before using WinGet."));

        return options;
    }
}

public sealed record InstallationPlan(string Kind, string Action, bool ExecutesPublisherCode);
public sealed record RepositoryInstallOption(string Ecosystem, string Tool, string Guidance);
