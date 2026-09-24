namespace GitHubAutoInstaller.Models;

/// <summary>
/// Ecosystems identified within a repository.
/// </summary>
public enum RepositoryEcosystem
{
    Unknown,
    GitHubRelease,
    WinGet,
    DotNet,
    Rust,
    NodeJs,
    Python,
    Docker,
    PowerShellScript,
    SourceOnly
}
