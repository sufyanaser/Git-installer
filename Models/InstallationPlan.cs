namespace GitHubAutoInstaller.Models;

/// <summary>
/// A fully inspectable, typed installation plan specifying exact proposed operations
/// prior to any execution or download.
/// </summary>
public sealed class InstallationPlan
{
    public required GitHubRepository Repository { get; init; }
    public required string Version { get; init; }
    public required RepositoryEcosystem Ecosystem { get; init; }
    public required InstallationMethod Method { get; init; }
    public string TargetArchitecture { get; init; } = "win-x64";
    public IReadOnlyList<ToolRequirement> RequiredTools { get; init; } = [];
    public Uri? DownloadSource { get; init; }
    public ReleaseAsset? TargetAsset { get; init; }
    public required IntegrityVerification VerificationMethod { get; init; }
    public IReadOnlyList<ProposedCommand> ProposedCommands { get; init; } = [];
    public RequiredPermissionLevel RequiredPermissions { get; init; } = RequiredPermissionLevel.StandardUser;
    public string? InstallationDestination { get; init; }
    public required string ExpectedVerificationProcedure { get; init; }
    public required string RollbackStrategy { get; init; }
    public IReadOnlyList<string> SecurityWarnings { get; init; } = [];
    public required bool CanExecuteAutomatically { get; init; }
    public string? BlockedReason { get; init; }
    public required string DisplayTitle { get; init; }
    public required string Summary { get; init; }

    public bool ExecutesPublisherCode =>
        Method is InstallationMethod.WindowsExecutableExe
            or InstallationMethod.WindowsInstallerMsi
            or InstallationMethod.PowerShellScript;

    public string Kind => DisplayTitle;
    public string Action => Summary;
}
