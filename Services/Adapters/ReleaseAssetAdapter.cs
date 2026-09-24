using System.IO;
using GitHubAutoInstaller.Models;

namespace GitHubAutoInstaller.Services.Adapters;

/// <summary>
/// Executes installations from downloaded release assets (EXE, MSI, ZIP, 7z, PS1).
/// </summary>
public sealed class ReleaseAssetAdapter : IInstallationAdapter
{
    private readonly AssetInstallerService _installerService;

    public ReleaseAssetAdapter(AssetInstallerService installerService)
    {
        _installerService = installerService ?? throw new ArgumentNullException(nameof(installerService));
    }

    public InstallationMethod Method => InstallationMethod.WindowsExecutableExe;

    public bool CanHandle(InstallationPlan plan) =>
        plan.Method is InstallationMethod.WindowsExecutableExe
            or InstallationMethod.WindowsInstallerMsi
            or InstallationMethod.PortableZipArchive
            or InstallationMethod.Portable7zArchive
            or InstallationMethod.PowerShellScript;

    public async Task<InstallationResult> ExecuteAsync(
        InstallationPlan plan,
        string? downloadedAssetPath,
        bool silent,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (string.IsNullOrWhiteSpace(downloadedAssetPath) || !File.Exists(downloadedAssetPath))
        {
            throw new InvalidOperationException("Downloaded asset file is missing or was not specified.");
        }

        log($"Installing {Path.GetFileName(downloadedAssetPath)} using release asset adapter...");
        return await _installerService.InstallAsync(
            downloadedAssetPath,
            plan.Repository.Name,
            silent,
            log,
            cancellationToken);
    }
}
