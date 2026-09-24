using System.IO;
using GitHubAutoInstaller.Models;

namespace GitHubAutoInstaller.Services;

/// <summary>
/// Produces inspectable installation plans without executing downloaded content or running arbitrary commands.
/// A repository manifest is evidence of a build ecosystem, not permission to run its scripts.
/// </summary>
public static class InstallationPlanService
{
    private static readonly string ToolsBaseDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GitHubTools");

    public static InstallationPlan ForReleaseAsset(
        ReleaseAsset asset,
        GitHubRepository? repository = null,
        string? version = null)
    {
        ArgumentNullException.ThrowIfNull(asset);
        repository ??= new GitHubRepository("unknown", Path.GetFileNameWithoutExtension(asset.Name));
        version ??= "latest";

        string extension = Path.GetExtension(asset.Name).ToLowerInvariant();
        string installTarget = Path.Combine(ToolsBaseDirectory, SanitizeFolderName(repository.Name));

        return extension switch
        {
            ".exe" => new InstallationPlan
            {
                Repository = repository,
                Version = version,
                Ecosystem = RepositoryEcosystem.GitHubRelease,
                Method = InstallationMethod.WindowsExecutableExe,
                TargetArchitecture = "win-x64",
                TargetAsset = asset,
                DownloadSource = asset.DownloadUrl,
                VerificationMethod = new(IntegrityVerificationKind.AuthenticodeSignature, null, "SHA256", "Windows Authenticode digital signature or publisher checksum."),
                ProposedCommands = [
                    new ProposedCommand(asset.Name, [], $"<downloaded-installer>\\{asset.Name}", "Execute the installer interactively or with recognized silent switches.")
                ],
                RequiredPermissions = RequiredPermissionLevel.StandardUser,
                InstallationDestination = "Managed by Windows / Installer",
                ExpectedVerificationProcedure = "Verify process exit code 0 or system restart required (1641/3010).",
                RollbackStrategy = "Uninstall via Windows Settings > Installed Apps if installation fails.",
                SecurityWarnings = [
                    "Executes publisher-provided binary code with user permissions.",
                    "Ensure you trust the repository author before executing."
                ],
                CanExecuteAutomatically = true,
                DisplayTitle = "Windows executable installer",
                Summary = "Download and launch the publisher's executable interactively."
            },
            ".msi" => new InstallationPlan
            {
                Repository = repository,
                Version = version,
                Ecosystem = RepositoryEcosystem.GitHubRelease,
                Method = InstallationMethod.WindowsInstallerMsi,
                TargetArchitecture = "win-x64",
                TargetAsset = asset,
                DownloadSource = asset.DownloadUrl,
                VerificationMethod = new(IntegrityVerificationKind.AuthenticodeSignature, null, "SHA256", "Windows Authenticode digital signature or publisher checksum."),
                ProposedCommands = [
                    new ProposedCommand("msiexec.exe", ["/i", asset.Name], $"msiexec.exe /i \"{asset.Name}\"", "Install Windows Installer package via Windows Installer service.")
                ],
                RequiredPermissions = RequiredPermissionLevel.StandardUser,
                InstallationDestination = "Managed by Windows Installer",
                ExpectedVerificationProcedure = "Verify msiexec returns exit code 0 or restart required (1641/3010).",
                RollbackStrategy = "MSI transaction rollback will restore previous state upon failure.",
                SecurityWarnings = [
                    "Windows Installer executes installation actions with Windows Installer privileges.",
                    "Ensure you trust the package publisher."
                ],
                CanExecuteAutomatically = true,
                DisplayTitle = "Windows Installer package",
                Summary = "Download and launch the MSI using Windows Installer."
            },
            ".zip" => new InstallationPlan
            {
                Repository = repository,
                Version = version,
                Ecosystem = RepositoryEcosystem.GitHubRelease,
                Method = InstallationMethod.PortableZipArchive,
                TargetArchitecture = "win-x64",
                TargetAsset = asset,
                DownloadSource = asset.DownloadUrl,
                VerificationMethod = new(IntegrityVerificationKind.PublisherChecksum, null, "SHA256", "Publisher checksum verification or local SHA-256 audit logging."),
                ProposedCommands = [
                    new ProposedCommand("internal:zip-extract", ["--destination", installTarget], $"Extract archive to {installTarget}", "Extract safe archive entries atomically.")
                ],
                RequiredPermissions = RequiredPermissionLevel.StandardUser,
                InstallationDestination = installTarget,
                ExpectedVerificationProcedure = "Verify destination directory exists and contains target Windows executable.",
                RollbackStrategy = "Atomic directory swap restores backup directory if extraction fails.",
                SecurityWarnings = [
                    "Enforces 4 GB extraction limit, 10,000 entries limit, and directory traversal rejection.",
                    "Portable archive does not register in Windows Add/Remove Programs."
                ],
                CanExecuteAutomatically = true,
                DisplayTitle = "Portable ZIP archive",
                Summary = "Download, validate archive paths and extract to the application directory."
            },
            ".7z" => new InstallationPlan
            {
                Repository = repository,
                Version = version,
                Ecosystem = RepositoryEcosystem.GitHubRelease,
                Method = InstallationMethod.Portable7zArchive,
                TargetArchitecture = "win-x64",
                TargetAsset = asset,
                DownloadSource = asset.DownloadUrl,
                VerificationMethod = new(IntegrityVerificationKind.PublisherChecksum, null, "SHA256", "Publisher checksum verification or local SHA-256 audit logging."),
                ProposedCommands = [
                    new ProposedCommand("internal:7z-extract", ["--destination", installTarget], $"Extract 7z archive to {installTarget}", "Extract safe 7z entries atomically.")
                ],
                RequiredPermissions = RequiredPermissionLevel.StandardUser,
                InstallationDestination = installTarget,
                ExpectedVerificationProcedure = "Verify destination directory exists and contains target Windows executable.",
                RollbackStrategy = "Atomic directory swap restores backup directory if extraction fails.",
                SecurityWarnings = [
                    "Enforces 4 GB extraction limit, 10,000 entries limit, and directory traversal rejection.",
                    "Portable archive does not register in Windows Add/Remove Programs."
                ],
                CanExecuteAutomatically = true,
                DisplayTitle = "Portable 7z archive",
                Summary = "Download, validate 7z archive paths and extract to the application directory safely."
            },
            ".ps1" => new InstallationPlan
            {
                Repository = repository,
                Version = version,
                Ecosystem = RepositoryEcosystem.GitHubRelease,
                Method = InstallationMethod.PowerShellScript,
                TargetArchitecture = "win-x64",
                TargetAsset = asset,
                DownloadSource = asset.DownloadUrl,
                VerificationMethod = new(IntegrityVerificationKind.LocalSha256Audit, null, "SHA256", "Local SHA-256 audit record."),
                ProposedCommands = [
                    new ProposedCommand("powershell.exe", ["-NoLogo", "-NoProfile", "-File", asset.Name], $"powershell.exe -NoLogo -NoProfile -File \"{asset.Name}\"", "Execute installation script in isolated session.")
                ],
                RequiredPermissions = RequiredPermissionLevel.StandardUser,
                InstallationDestination = "Determined by script",
                ExpectedVerificationProcedure = "Verify powershell process completes with exit code 0.",
                RollbackStrategy = "Manual cleanup depending on script actions.",
                SecurityWarnings = [
                    "PowerShell scripts can execute arbitrary PowerShell and .NET code.",
                    "Script will be executed in a restricted -NoLogo -NoProfile session only after explicit confirmation."
                ],
                CanExecuteAutomatically = true,
                DisplayTitle = "PowerShell script",
                Summary = "Download and execute publisher-provided PowerShell code only after explicit approval."
            },
            _ => throw new InvalidOperationException($"Unsupported release asset: {asset.Name}")
        };
    }

    public static async Task<IReadOnlyList<InstallationOption>> GenerateOptionsAsync(
        GitHubRepository repository,
        string version,
        GitHubRelease? release,
        IReadOnlyList<string> rootFiles,
        ToolVerificationService toolVerifier,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        toolVerifier ??= new ToolVerificationService();
        rootFiles ??= [];

        List<InstallationOption> options = [];

        // 1. Check for official Windows release asset (Highest Priority)
        if (release is not null && release.Assets.Count > 0)
        {
            try
            {
                ReleaseAsset bestAsset = ReleaseAssetSelector.SelectBestWindowsX64Asset(release.Assets);
                InstallationPlan releasePlan = ForReleaseAsset(bestAsset, repository, release.TagName);
                options.Add(new InstallationOption(
                    Key: "official-release",
                    Title: $"Official Release ({bestAsset.Name})",
                    Description: $"Download official release asset {bestAsset.Name} from {release.TagName}",
                    Ecosystem: RepositoryEcosystem.GitHubRelease,
                    Method: releasePlan.Method,
                    IsRecommended: true,
                    Plan: releasePlan));
            }
            catch (InvalidOperationException)
            {
                // Release assets exist but not compatible for Windows x64
            }
        }

        HashSet<string> normalizedFiles = new(
            rootFiles.Where(f => !string.IsNullOrWhiteSpace(f) && !f.Contains('/'))
                     .Select(f => f.ToLowerInvariant()),
            StringComparer.OrdinalIgnoreCase);

        // 2. WinGet manifest option
        if (normalizedFiles.Contains("winget-pkgs.yaml") ||
            normalizedFiles.Contains("winget.yaml") ||
            rootFiles.Any(f => f.StartsWith(".winget", StringComparison.OrdinalIgnoreCase)))
        {
            ToolRequirement wingetTool = await toolVerifier.CheckToolAsync("winget", cancellationToken: cancellationToken);
            string packageId = $"{repository.Owner}.{repository.Name}";
            InstallationPlan wingetPlan = new()
            {
                Repository = repository,
                Version = version,
                Ecosystem = RepositoryEcosystem.WinGet,
                Method = InstallationMethod.WinGetPackage,
                TargetArchitecture = "win-x64",
                RequiredTools = [wingetTool],
                VerificationMethod = new(IntegrityVerificationKind.PackageManagerVerified, null, null, "WinGet package manager verified manifest."),
                ProposedCommands = [
                    new ProposedCommand("winget.exe", ["install", "--id", packageId, "--exact", "--accept-package-agreements", "--accept-source-agreements"], $"winget.exe install --id {packageId} --exact --accept-package-agreements --accept-source-agreements", "Install via Windows Package Manager")
                ],
                RequiredPermissions = RequiredPermissionLevel.StandardUser,
                InstallationDestination = "Managed by WinGet",
                ExpectedVerificationProcedure = "Verify winget command exits with code 0.",
                RollbackStrategy = "winget uninstall --id " + packageId,
                SecurityWarnings = ["Packages installed via WinGet run according to the publisher manifest."],
                CanExecuteAutomatically = wingetTool.IsInstalled,
                BlockedReason = wingetTool.IsInstalled ? null : "WinGet is not installed or not in PATH.",
                DisplayTitle = "WinGet package",
                Summary = $"Install {packageId} via Windows Package Manager."
            };

            options.Add(new InstallationOption(
                Key: "winget",
                Title: "WinGet Package",
                Description: $"Install using WinGet package identifier '{packageId}'",
                Ecosystem: RepositoryEcosystem.WinGet,
                Method: InstallationMethod.WinGetPackage,
                IsRecommended: options.Count == 0,
                Plan: wingetPlan));
        }

        // 3. Python pip option
        if (normalizedFiles.Contains("pyproject.toml") || normalizedFiles.Contains("setup.py") || normalizedFiles.Contains("requirements.txt"))
        {
            ToolRequirement pythonTool = await toolVerifier.CheckToolAsync("python", cancellationToken: cancellationToken);
            ToolRequirement pipTool = await toolVerifier.CheckToolAsync("pip", cancellationToken: cancellationToken);
            bool toolsReady = pythonTool.IsInstalled && pipTool.IsInstalled;

            InstallationPlan pipPlan = new()
            {
                Repository = repository,
                Version = version,
                Ecosystem = RepositoryEcosystem.Python,
                Method = InstallationMethod.PipPackage,
                TargetArchitecture = "any (Python)",
                RequiredTools = [pythonTool, pipTool],
                VerificationMethod = new(IntegrityVerificationKind.PackageManagerVerified, null, null, "PyPI / pip package integrity check."),
                ProposedCommands = [
                    new ProposedCommand("pip.exe", ["install", "--no-cache-dir", repository.Name], $"pip install --no-cache-dir {repository.Name}", "Install Python package via pip")
                ],
                RequiredPermissions = RequiredPermissionLevel.StandardUser,
                InstallationDestination = "Python site-packages / Scripts",
                ExpectedVerificationProcedure = "Verify pip exits with code 0.",
                RollbackStrategy = $"pip uninstall -y {repository.Name}",
                SecurityWarnings = ["Python packages can execute setup scripts. Verify this is an application, not a library."],
                CanExecuteAutomatically = toolsReady,
                BlockedReason = toolsReady ? null : "Python or pip is not installed.",
                DisplayTitle = "Python package (pip)",
                Summary = $"Install Python package '{repository.Name}' using pip."
            };

            options.Add(new InstallationOption(
                Key: "pip",
                Title: "Python package (pip)",
                Description: "Install using Python's pip package manager",
                Ecosystem: RepositoryEcosystem.Python,
                Method: InstallationMethod.PipPackage,
                IsRecommended: options.Count == 0,
                Plan: pipPlan));
        }

        // 4. Node.js npm option
        if (normalizedFiles.Contains("package.json"))
        {
            ToolRequirement nodeTool = await toolVerifier.CheckToolAsync("node", cancellationToken: cancellationToken);
            ToolRequirement npmTool = await toolVerifier.CheckToolAsync("npm", cancellationToken: cancellationToken);
            bool toolsReady = nodeTool.IsInstalled && npmTool.IsInstalled;

            InstallationPlan npmPlan = new()
            {
                Repository = repository,
                Version = version,
                Ecosystem = RepositoryEcosystem.NodeJs,
                Method = InstallationMethod.NpmPackage,
                TargetArchitecture = "any (Node.js)",
                RequiredTools = [nodeTool, npmTool],
                VerificationMethod = new(IntegrityVerificationKind.PackageManagerVerified, null, null, "npm registry checksum check."),
                ProposedCommands = [
                    new ProposedCommand("npm.cmd", ["install", "-g", repository.Name.ToLowerInvariant()], $"npm install -g {repository.Name.ToLowerInvariant()}", "Install CLI package globally via npm")
                ],
                RequiredPermissions = RequiredPermissionLevel.StandardUser,
                InstallationDestination = "npm global prefix",
                ExpectedVerificationProcedure = "Verify npm command exits with code 0.",
                RollbackStrategy = $"npm uninstall -g {repository.Name.ToLowerInvariant()}",
                SecurityWarnings = ["npm global packages install executables into user PATH."],
                CanExecuteAutomatically = toolsReady,
                BlockedReason = toolsReady ? null : "Node.js or npm is not installed.",
                DisplayTitle = "Node.js package (npm)",
                Summary = $"Install global npm CLI package '{repository.Name}'."
            };

            options.Add(new InstallationOption(
                Key: "npm",
                Title: "Node.js package (npm)",
                Description: "Install global CLI application using npm",
                Ecosystem: RepositoryEcosystem.NodeJs,
                Method: InstallationMethod.NpmPackage,
                IsRecommended: options.Count == 0,
                Plan: npmPlan));
        }

        // 5. Rust Cargo option
        if (normalizedFiles.Contains("cargo.toml"))
        {
            ToolRequirement cargoTool = await toolVerifier.CheckToolAsync("cargo", cancellationToken: cancellationToken);

            InstallationPlan cargoPlan = new()
            {
                Repository = repository,
                Version = version,
                Ecosystem = RepositoryEcosystem.Rust,
                Method = InstallationMethod.CargoCrate,
                TargetArchitecture = "win-x64",
                RequiredTools = [cargoTool],
                VerificationMethod = new(IntegrityVerificationKind.LocalSha256Audit, null, null, "Cargo lockfile checksum verification."),
                ProposedCommands = [
                    new ProposedCommand("cargo.exe", ["install", "--git", $"https://github.com/{repository.FullName}.git", "--locked"], $"cargo install --git https://github.com/{repository.FullName}.git --locked", "Compile and install Rust binary crate")
                ],
                RequiredPermissions = RequiredPermissionLevel.StandardUser,
                InstallationDestination = "%USERPROFILE%\\.cargo\\bin",
                ExpectedVerificationProcedure = "Verify cargo exits with code 0 and binary exists in .cargo/bin.",
                RollbackStrategy = $"cargo uninstall {repository.Name.ToLowerInvariant()}",
                SecurityWarnings = ["Compiles Rust code from Git source. Requires C++ build tools or MSVC SDK."],
                CanExecuteAutomatically = cargoTool.IsInstalled,
                BlockedReason = cargoTool.IsInstalled ? null : "Cargo (Rust toolchain) is not installed.",
                DisplayTitle = "Rust binary crate (cargo)",
                Summary = $"Build and install binary crate from {repository.FullName} via Cargo."
            };

            options.Add(new InstallationOption(
                Key: "cargo",
                Title: "Rust binary crate (cargo)",
                Description: "Compile and install application from source using Cargo",
                Ecosystem: RepositoryEcosystem.Rust,
                Method: InstallationMethod.CargoCrate,
                IsRecommended: options.Count == 0,
                Plan: cargoPlan));
        }

        // 6. .NET Tool option
        if (normalizedFiles.Any(f => f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            ToolRequirement dotnetTool = await toolVerifier.CheckToolAsync("dotnet", cancellationToken: cancellationToken);

            InstallationPlan dotnetPlan = new()
            {
                Repository = repository,
                Version = version,
                Ecosystem = RepositoryEcosystem.DotNet,
                Method = InstallationMethod.DotNetTool,
                TargetArchitecture = "win-x64",
                RequiredTools = [dotnetTool],
                VerificationMethod = new(IntegrityVerificationKind.PackageManagerVerified, null, null, "NuGet package signature verification."),
                ProposedCommands = [
                    new ProposedCommand("dotnet.exe", ["tool", "install", "--global", repository.Name.ToLowerInvariant()], $"dotnet tool install --global {repository.Name.ToLowerInvariant()}", "Install global .NET tool via dotnet CLI")
                ],
                RequiredPermissions = RequiredPermissionLevel.StandardUser,
                InstallationDestination = "%USERPROFILE%\\.dotnet\\tools",
                ExpectedVerificationProcedure = "Verify dotnet tool install exits with code 0.",
                RollbackStrategy = $"dotnet tool uninstall --global {repository.Name.ToLowerInvariant()}",
                SecurityWarnings = [".NET global tools are executed under user privileges."],
                CanExecuteAutomatically = dotnetTool.IsInstalled,
                BlockedReason = dotnetTool.IsInstalled ? null : ".NET SDK is not installed.",
                DisplayTitle = ".NET tool",
                Summary = $"Install global .NET tool '{repository.Name}'."
            };

            options.Add(new InstallationOption(
                Key: "dotnet",
                Title: ".NET Tool (dotnet)",
                Description: "Install .NET application or global tool using dotnet CLI",
                Ecosystem: RepositoryEcosystem.DotNet,
                Method: InstallationMethod.DotNetTool,
                IsRecommended: options.Count == 0,
                Plan: dotnetPlan));
        }

        // 7. Docker setup plan (Guarded: never executes automatically)
        if (normalizedFiles.Contains("dockerfile") ||
            normalizedFiles.Contains("docker-compose.yml") ||
            normalizedFiles.Contains("compose.yaml"))
        {
            ToolRequirement dockerTool = await toolVerifier.CheckToolAsync("docker", cancellationToken: cancellationToken);

            InstallationPlan dockerPlan = new()
            {
                Repository = repository,
                Version = version,
                Ecosystem = RepositoryEcosystem.Docker,
                Method = InstallationMethod.DockerSetupPlan,
                TargetArchitecture = "Docker container (Linux / Windows)",
                RequiredTools = [dockerTool],
                VerificationMethod = new(IntegrityVerificationKind.None, null, null, "Docker image build verification."),
                ProposedCommands = [
                    new ProposedCommand("docker.exe", ["compose", "up", "--build", "-d"], "docker compose up --build -d", "Build and launch containerized application in background")
                ],
                RequiredPermissions = RequiredPermissionLevel.StandardUser,
                InstallationDestination = "Docker Desktop Engine",
                ExpectedVerificationProcedure = "Inspect running container status via 'docker ps'.",
                RollbackStrategy = "docker compose down",
                SecurityWarnings = [
                    "Docker container launching is guarded. The application presents a setup plan only.",
                    "Do not automatically launch containers without manual review."
                ],
                CanExecuteAutomatically = false,
                BlockedReason = "Docker projects receive a proposed setup plan rather than automated execution.",
                DisplayTitle = "Docker container setup plan",
                Summary = "Review proposed Docker setup commands. Automated execution is intentionally guarded."
            };

            options.Add(new InstallationOption(
                Key: "docker",
                Title: "Docker Setup Plan",
                Description: "Containerized project. Provides inspectable setup instructions.",
                Ecosystem: RepositoryEcosystem.Docker,
                Method: InstallationMethod.DockerSetupPlan,
                IsRecommended: options.Count == 0,
                Plan: dockerPlan));
        }

        // If no options exist, add an informative unsupported plan
        if (options.Count == 0)
        {
            InstallationPlan unsupportedPlan = new()
            {
                Repository = repository,
                Version = version,
                Ecosystem = RepositoryEcosystem.SourceOnly,
                Method = InstallationMethod.ManualInstructions,
                TargetArchitecture = "Unknown",
                RequiredTools = [],
                VerificationMethod = new(IntegrityVerificationKind.None, null, null, "Manual verification required."),
                ProposedCommands = [],
                RequiredPermissions = RequiredPermissionLevel.StandardUser,
                InstallationDestination = "None",
                ExpectedVerificationProcedure = "Consult repository documentation.",
                RollbackStrategy = "None",
                SecurityWarnings = [
                    "Universal installation of every GitHub repository is not supported.",
                    "This repository lacks Windows release assets or automated package manifests."
                ],
                CanExecuteAutomatically = false,
                BlockedReason = "Repository does not contain compatible Windows releases or automated installation manifests.",
                DisplayTitle = "Manual installation required",
                Summary = "This repository cannot be installed automatically. Refer to its documentation."
            };

            options.Add(new InstallationOption(
                Key: "manual",
                Title: "Manual Setup Required",
                Description: "No automated Windows installation method found",
                Ecosystem: RepositoryEcosystem.SourceOnly,
                Method: InstallationMethod.ManualInstructions,
                IsRecommended: false,
                Plan: unsupportedPlan));
        }

        return options;
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

    private static string SanitizeFolderName(string name) =>
        new(name.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.').ToArray());
}

public sealed record RepositoryInstallOption(string Ecosystem, string Tool, string Guidance);
