namespace GitHubAutoInstaller.Models;

/// <summary>
/// Architectural and deployment classification of a repository.
/// </summary>
public enum RepositoryCategory
{
    /// <summary>
    /// Has published Windows x64 EXE, MSI, or validated package manager distribution.
    /// </summary>
    DirectlyInstallableApplication,

    /// <summary>
    /// Portable application distributed as a ZIP or 7z archive.
    /// </summary>
    PortableApplication,

    /// <summary>
    /// PowerShell-based installation script requiring explicit execution approval.
    /// </summary>
    ScriptInstaller,

    /// <summary>
    /// Development library or package intended for import rather than standalone execution.
    /// </summary>
    PackageOrLibrary,

    /// <summary>
    /// Source repository requiring compilation toolchain (e.g. C++, Rust, .NET SDK).
    /// </summary>
    RequiresCompilation,

    /// <summary>
    /// Application requires external runtime/engine (Python, Node.js, Docker, etc.).
    /// </summary>
    RequiresExternalDependencies,

    /// <summary>
    /// Cannot reliably identify a safe automated installation mechanism.
    /// </summary>
    UnsupportedOrAmbiguous
}
