using System.IO;
using GitHubAutoInstaller.Models;

namespace GitHubAutoInstaller.Services;

/// <summary>
/// Inspects repository structure, manifests, and release assets to classify deployment capabilities,
/// detect ecosystems, and evaluate Windows compatibility.
/// </summary>
public static class RepositoryInspectorService
{
    public static RepositoryClassification Classify(
        GitHubRepository repository,
        GitHubRelease? latestRelease,
        IReadOnlyList<string> rootFiles,
        string? manifestSnippet = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        rootFiles ??= [];

        HashSet<string> normalizedFiles = new(
            rootFiles.Where(f => !string.IsNullOrWhiteSpace(f) && !f.Contains('/'))
                     .Select(f => f.ToLowerInvariant()),
            StringComparer.OrdinalIgnoreCase);

        List<RepositoryEcosystem> ecosystems = [];
        List<string> requiredTools = [];

        // 1. Inspect Published GitHub Releases
        ReleaseAsset? bestAsset = null;
        if (latestRelease is not null && latestRelease.Assets.Count > 0)
        {
            try
            {
                bestAsset = ReleaseAssetSelector.SelectBestWindowsX64Asset(latestRelease.Assets);
                ecosystems.Add(RepositoryEcosystem.GitHubRelease);
            }
            catch (InvalidOperationException)
            {
                // Release exists but lacks compatible Windows assets
            }
        }

        // 2. Inspect WinGet manifests
        if (normalizedFiles.Contains("winget-pkgs.yaml") ||
            normalizedFiles.Contains("winget.yaml") ||
            rootFiles.Any(f => f.StartsWith(".winget", StringComparison.OrdinalIgnoreCase)))
        {
            ecosystems.Add(RepositoryEcosystem.WinGet);
            requiredTools.Add("winget");
        }

        // 3. Inspect Python projects
        bool hasPython = normalizedFiles.Contains("pyproject.toml") ||
                         normalizedFiles.Contains("setup.py") ||
                         normalizedFiles.Contains("requirements.txt");
        if (hasPython)
        {
            ecosystems.Add(RepositoryEcosystem.Python);
            requiredTools.Add("python");
            requiredTools.Add("pip");
        }

        // 4. Inspect Node.js projects
        bool hasNode = normalizedFiles.Contains("package.json");
        if (hasNode)
        {
            ecosystems.Add(RepositoryEcosystem.NodeJs);
            requiredTools.Add("node");
            requiredTools.Add("npm");
        }

        // 5. Inspect Rust projects
        bool hasRust = normalizedFiles.Contains("cargo.toml");
        if (hasRust)
        {
            ecosystems.Add(RepositoryEcosystem.Rust);
            requiredTools.Add("cargo");
        }

        // 6. Inspect .NET projects
        bool hasDotNet = normalizedFiles.Any(f => f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                                                  f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
        if (hasDotNet)
        {
            ecosystems.Add(RepositoryEcosystem.DotNet);
            requiredTools.Add("dotnet");
        }

        // 7. Inspect Docker projects
        bool hasDocker = normalizedFiles.Contains("dockerfile") ||
                         normalizedFiles.Contains("docker-compose.yml") ||
                         normalizedFiles.Contains("docker-compose.yaml") ||
                         normalizedFiles.Contains("compose.yaml") ||
                         normalizedFiles.Contains("compose.yml");
        if (hasDocker)
        {
            ecosystems.Add(RepositoryEcosystem.Docker);
            requiredTools.Add("docker");
        }

        // 8. Inspect PowerShell script in root
        bool hasRootPowerShell = normalizedFiles.Contains("install.ps1") ||
                                 normalizedFiles.Contains("setup.ps1");
        if (hasRootPowerShell)
        {
            ecosystems.Add(RepositoryEcosystem.PowerShellScript);
            requiredTools.Add("powershell");
        }

        if (ecosystems.Count == 0)
        {
            ecosystems.Add(RepositoryEcosystem.SourceOnly);
        }

        // Determine Primary Category & Compatibility
        if (bestAsset is not null)
        {
            string ext = Path.GetExtension(bestAsset.Name).ToLowerInvariant();
            if (ext is ".zip" or ".7z")
            {
                return new RepositoryClassification(
                    Category: RepositoryCategory.PortableApplication,
                    CategoryDescription: $"Official Windows {ext[1..].ToUpperInvariant()} portable release archive available.",
                    DetectedEcosystems: ecosystems,
                    IsWindowsCompatible: true,
                    WindowsCompatibilityDetails: "Windows x64 compatible release asset verified.",
                    RequiredExternalTools: [],
                    CanInstallAutomatically: true,
                    UnsupportedReason: null);
            }

            if (ext is ".ps1")
            {
                return new RepositoryClassification(
                    Category: RepositoryCategory.ScriptInstaller,
                    CategoryDescription: "Official Windows PowerShell setup script available in release.",
                    DetectedEcosystems: ecosystems,
                    IsWindowsCompatible: true,
                    WindowsCompatibilityDetails: "Requires Windows PowerShell host.",
                    RequiredExternalTools: ["powershell"],
                    CanInstallAutomatically: true,
                    UnsupportedReason: null);
            }

            return new RepositoryClassification(
                Category: RepositoryCategory.DirectlyInstallableApplication,
                CategoryDescription: $"Official Windows {ext[1..].ToUpperInvariant()} installer available in latest release.",
                DetectedEcosystems: ecosystems,
                IsWindowsCompatible: true,
                WindowsCompatibilityDetails: "Windows x64 standalone executable or installer package.",
                RequiredExternalTools: [],
                CanInstallAutomatically: true,
                UnsupportedReason: null);
        }

        // No compatible Windows release asset exists
        if (ecosystems.Contains(RepositoryEcosystem.WinGet))
        {
            return new RepositoryClassification(
                Category: RepositoryCategory.DirectlyInstallableApplication,
                CategoryDescription: "WinGet package manifest detected in repository.",
                DetectedEcosystems: ecosystems,
                IsWindowsCompatible: true,
                WindowsCompatibilityDetails: "Requires Windows Package Manager (winget).",
                RequiredExternalTools: ["winget"],
                CanInstallAutomatically: true,
                UnsupportedReason: null);
        }

        if (hasDocker && ecosystems.Count == 1)
        {
            return new RepositoryClassification(
                Category: RepositoryCategory.RequiresExternalDependencies,
                CategoryDescription: "Docker containerized project. Automated container launching is guarded.",
                DetectedEcosystems: ecosystems,
                IsWindowsCompatible: true,
                WindowsCompatibilityDetails: "Requires Docker Desktop on Windows.",
                RequiredExternalTools: ["docker"],
                CanInstallAutomatically: false,
                UnsupportedReason: "Docker projects receive a proposed setup plan rather than automatic container launching.");
        }

        // Check if manifest snippet indicates a library or package
        if (hasPython || hasNode || hasRust || hasDotNet)
        {
            bool isExplicitLibrary = IsKnownLibraryOrPackage(repository.Name, manifestSnippet);
            if (isExplicitLibrary)
            {
                return new RepositoryClassification(
                    Category: RepositoryCategory.PackageOrLibrary,
                    CategoryDescription: "Software development library or package intended for import rather than standalone execution.",
                    DetectedEcosystems: ecosystems,
                    IsWindowsCompatible: true,
                    WindowsCompatibilityDetails: "Development package; not an end-user Windows desktop application.",
                    RequiredExternalTools: requiredTools,
                    CanInstallAutomatically: false,
                    UnsupportedReason: $"{repository.FullName} is a library or development package, not a standalone desktop application.");
            }

            return new RepositoryClassification(
                Category: RepositoryCategory.RequiresExternalDependencies,
                CategoryDescription: "Source-based project requiring development toolchains and package managers.",
                DetectedEcosystems: ecosystems,
                IsWindowsCompatible: true,
                WindowsCompatibilityDetails: "Can be installed or run via local language package managers if installed.",
                RequiredExternalTools: requiredTools,
                CanInstallAutomatically: true,
                UnsupportedReason: null);
        }

        if (latestRelease is not null)
        {
            return new RepositoryClassification(
                Category: RepositoryCategory.UnsupportedOrAmbiguous,
                CategoryDescription: "GitHub release found, but no compatible Windows x64 assets were uploaded.",
                DetectedEcosystems: ecosystems,
                IsWindowsCompatible: false,
                WindowsCompatibilityDetails: "No Windows x64 binaries, installers, or portable archives found in latest release.",
                RequiredExternalTools: [],
                CanInstallAutomatically: false,
                UnsupportedReason: "The latest published release does not contain any Windows x64 installers (.exe, .msi) or archives (.zip, .7z).");
        }

        return new RepositoryClassification(
            Category: RepositoryCategory.UnsupportedOrAmbiguous,
            CategoryDescription: "Source-only repository without recognized installation manifests or Windows releases.",
            DetectedEcosystems: ecosystems,
            IsWindowsCompatible: false,
            WindowsCompatibilityDetails: "No Windows installers, manifests, or recognized build ecosystems found.",
            RequiredExternalTools: [],
            CanInstallAutomatically: false,
            UnsupportedReason: "Repository does not contain published Windows releases or automated installation manifests.");
    }

    private static bool IsKnownLibraryOrPackage(string repositoryName, string? manifestSnippet)
    {
        if (manifestSnippet is not null)
        {
            string lowerSnippet = manifestSnippet.ToLowerInvariant();
            if (lowerSnippet.Contains("[lib]") && !lowerSnippet.Contains("[[bin]]"))
            {
                return true;
            }

            if (lowerSnippet.Contains("\"private\": true") || lowerSnippet.Contains("\"main\":") && !lowerSnippet.Contains("\"bin\":"))
            {
                return true;
            }
        }

        string lowerName = repositoryName.ToLowerInvariant();
        return lowerName.EndsWith("-lib", StringComparison.Ordinal) ||
               lowerName.EndsWith("-core", StringComparison.Ordinal) ||
               lowerName.EndsWith("-sdk", StringComparison.Ordinal) ||
               lowerName.StartsWith("lib", StringComparison.Ordinal);
    }
}
