using System.IO;
using GitHubAutoInstaller.Models;
using GitHubAutoInstaller.Services.Adapters;

namespace GitHubAutoInstaller.Services;

/// <summary>
/// Orchestrates repository installation through dedicated adapters, enforcing pre-flight dependency checks,
/// integrity validation, safe process execution, and post-installation verification.
/// </summary>
public sealed class InstallationEngine
{
    private readonly FileDownloadService _downloadService;
    private readonly ToolVerificationService _toolVerifier;
    private readonly IReadOnlyList<IInstallationAdapter> _adapters;

    public InstallationEngine(
        FileDownloadService downloadService,
        AssetInstallerService assetInstaller,
        ToolVerificationService? toolVerifier = null,
        IEnumerable<IInstallationAdapter>? customAdapters = null)
    {
        _downloadService = downloadService ?? throw new ArgumentNullException(nameof(downloadService));
        ArgumentNullException.ThrowIfNull(assetInstaller);
        _toolVerifier = toolVerifier ?? new ToolVerificationService();

        _adapters = customAdapters?.ToList() ?? [
            new ReleaseAssetAdapter(assetInstaller),
            new WinGetAdapter(_toolVerifier),
            new PipAdapter(_toolVerifier),
            new NpmAdapter(_toolVerifier),
            new CargoAdapter(_toolVerifier),
            new DotNetToolAdapter(_toolVerifier),
            new GuardedPlanAdapter()
        ];
    }

    public async Task<InstallationResult> ExecutePlanAsync(
        InstallationPlan plan,
        string downloadDirectory,
        bool silent,
        IProgress<double> progress,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadDirectory);
        log ??= _ => { };
        progress ??= new Progress<double>();

        // 1. Verify Plan Pre-conditions
        if (!plan.CanExecuteAutomatically)
        {
            throw new InvalidOperationException(
                plan.BlockedReason ?? "Automated execution is guarded or unsupported for this installation plan.");
        }

        foreach (ToolRequirement req in plan.RequiredTools)
        {
            if (!req.IsInstalled)
            {
                throw new InvalidOperationException(
                    $"Cannot execute plan because required tool '{req.ToolName}' is missing. {req.ResolutionGuidance}");
            }
        }

        // 2. Select Adapter
        IInstallationAdapter adapter = _adapters.FirstOrDefault(a => a.CanHandle(plan))
            ?? throw new InvalidOperationException($"No installation adapter found for method '{plan.Method}'.");

        string? downloadedPath = null;

        // 3. Download & Verify if asset-based
        if (plan.TargetAsset is not null)
        {
            log($"Initiating HTTPS download for {plan.TargetAsset.Name}...");
            downloadedPath = await _downloadService.DownloadAsync(
                plan.TargetAsset,
                downloadDirectory,
                progress,
                cancellationToken);

            log($"Downloaded to: {downloadedPath}");

            // Verify Authenticode signature if PE executable/MSI
            string extension = Path.GetExtension(downloadedPath).ToLowerInvariant();
            if (extension is ".exe" or ".msi")
            {
                AuthenticodeResult auth = IntegrityVerificationService.VerifyAuthenticode(downloadedPath);
                if (auth.IsSigned)
                {
                    log($"Authenticode digital signature: {auth.Details}");
                }
                else
                {
                    log("Authenticode notice: Executable is not digitally signed by a known publisher.");
                }
            }

            // Verify Checksum
            if (plan.VerificationMethod.Kind == IntegrityVerificationKind.PublisherChecksum &&
                !string.IsNullOrWhiteSpace(plan.VerificationMethod.ExpectedHash))
            {
                await IntegrityVerificationService.VerifyPublisherChecksumAsync(
                    downloadedPath,
                    plan.VerificationMethod.ExpectedHash,
                    cancellationToken);
                log("Publisher checksum verified successfully.");
            }
            else
            {
                string localSha256 = await IntegrityVerificationService.ComputeSha256Async(
                    downloadedPath,
                    cancellationToken);
                log($"SHA-256: {localSha256} (Local audit hash; no publisher checksum was declared for comparison).");
            }
        }

        // 4. Execute Installation via Adapter
        log($"Starting installation via {adapter.GetType().Name}...");
        InstallationResult result = await adapter.ExecuteAsync(
            plan,
            downloadedPath,
            silent,
            log,
            cancellationToken);

        // 5. Post-Installation Verification
        VerifyInstallationResult(plan, result);

        log("Installation verified successfully.");
        return result;
    }

    private static void VerifyInstallationResult(InstallationPlan plan, InstallationResult result)
    {
        if (result.ExitCode is not (0 or 1_641 or 3_010))
        {
            throw new InvalidOperationException($"Installation failed with exit code {result.ExitCode}.");
        }

        if (plan.Method is InstallationMethod.PortableZipArchive or InstallationMethod.Portable7zArchive)
        {
            if (string.IsNullOrWhiteSpace(result.InstalledDirectory) || !Directory.Exists(result.InstalledDirectory))
            {
                throw new InvalidOperationException("Installation verification failed: Extracted directory does not exist.");
            }

            bool hasFiles = Directory.EnumerateFileSystemEntries(result.InstalledDirectory).Any();
            if (!hasFiles)
            {
                throw new InvalidOperationException("Installation verification failed: Extracted directory is empty.");
            }
        }
    }
}
