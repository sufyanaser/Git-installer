namespace GitHubAutoInstaller.Models;

public enum InstallationMethod
{
    WindowsInstallerMsi,
    WindowsExecutableExe,
    PortableZipArchive,
    Portable7zArchive,
    PowerShellScript,
    WinGetPackage,
    PipPackage,
    NpmPackage,
    CargoCrate,
    DotNetTool,
    DockerSetupPlan,
    ManualInstructions
}
