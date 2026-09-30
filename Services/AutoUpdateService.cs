using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using GitHubAutoInstaller.Models;

namespace GitHubAutoInstaller.Services;

public sealed class AutoUpdateService
{
    public const string DefaultRepositoryOwner = "sufyanaser";
    public const string DefaultRepositoryName = "Git-installer";

    private readonly HttpClient _httpClient;
    private readonly GitHubReleaseService _releaseService;
    private readonly FileDownloadService _downloadService;
    private readonly GitHubRepository _repository;
    private readonly string _currentVersion;
    private readonly string _updateTempDirectory;

    public AutoUpdateService(
        HttpClient httpClient,
        GitHubReleaseService releaseService,
        FileDownloadService downloadService,
        GitHubRepository? repository = null,
        string? currentVersion = null,
        string? updateTempDirectory = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _releaseService = releaseService ?? throw new ArgumentNullException(nameof(releaseService));
        _downloadService = downloadService ?? throw new ArgumentNullException(nameof(downloadService));

        string envRepo = Environment.GetEnvironmentVariable("GITHUB_AUTO_INSTALLER_REPO")?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(envRepo))
        {
            _repository = GitHubRepositoryParser.Parse(envRepo);
        }
        else
        {
            _repository = repository ?? new GitHubRepository(DefaultRepositoryOwner, DefaultRepositoryName);
        }

        _currentVersion = !string.IsNullOrWhiteSpace(currentVersion)
            ? currentVersion.TrimStart('v', 'V')
            : GetApplicationVersion();

        _updateTempDirectory = updateTempDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GitHubAutoInstaller",
            "Updates");
    }

    public GitHubRepository Repository => _repository;
    public string CurrentVersion => _currentVersion;
    public string UpdateTempDirectory => _updateTempDirectory;

    public async Task<UpdateInfo> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            GitHubRelease? latestRelease = await _releaseService.GetNewestReleaseOrNullAsync(_repository, cancellationToken);
            if (latestRelease is null)
            {
                return UpdateInfo.None(_currentVersion);
            }

            string rawTag = latestRelease.TagName.TrimStart('v', 'V').Trim();
            bool isNewer = IsNewerVersion(rawTag, _currentVersion);

            ReleaseAsset? installerAsset = latestRelease.Assets.FirstOrDefault(a =>
                (a.Name.Contains("setup", StringComparison.OrdinalIgnoreCase) || a.Name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)) &&
                !ContainsDisallowedArchitecture(a.Name));

            ReleaseAsset? standaloneAsset = latestRelease.Assets.FirstOrDefault(a =>
                !a.Name.Contains("setup", StringComparison.OrdinalIgnoreCase) &&
                a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                !ContainsDisallowedArchitecture(a.Name));

            ReleaseAsset? bestAsset = installerAsset ?? standaloneAsset ?? FindBestUpdateAsset(latestRelease.Assets);
            if (installerAsset is null && bestAsset is not null && (bestAsset.Name.Contains("setup", StringComparison.OrdinalIgnoreCase) || bestAsset.Name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)))
            {
                installerAsset = bestAsset;
            }
            if (standaloneAsset is null && bestAsset is not null && !bestAsset.Name.Contains("setup", StringComparison.OrdinalIgnoreCase) && bestAsset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                standaloneAsset = bestAsset;
            }

            ReleaseAsset? checksumAsset = latestRelease.Assets.FirstOrDefault(a =>
                a.Name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase) ||
                a.Name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase) ||
                a.Name.EndsWith(".sha256sums", StringComparison.OrdinalIgnoreCase));

            return new UpdateInfo(
                IsUpdateAvailable: isNewer && (installerAsset is not null || standaloneAsset is not null),
                CurrentVersion: _currentVersion,
                LatestVersion: rawTag,
                ReleaseNotes: $"Release {latestRelease.TagName}",
                ReleasePageUrl: latestRelease.PageUrl,
                InstallerAsset: installerAsset ?? standaloneAsset,
                StandaloneAsset: standaloneAsset,
                ChecksumAsset: checksumAsset,
                PublishedAt: null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return UpdateInfo.None(_currentVersion);
        }
    }

    public async Task<string> DownloadAndVerifyUpdateAsync(
        UpdateInfo update,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        ReleaseAsset targetAsset = update.InstallerAsset ?? update.StandaloneAsset
            ?? throw new InvalidOperationException("No installer or executable asset found for this update.");

        Directory.CreateDirectory(_updateTempDirectory);
        IProgress<double> downloadProgress = progress ?? new Progress<double>();

        // Download target binary
        string downloadedFilePath = await _downloadService.DownloadAsync(
            targetAsset,
            _updateTempDirectory,
            downloadProgress,
            cancellationToken);

        // Checksum verification if SHA256SUMS is declared in release
        if (update.ChecksumAsset is not null)
        {
            string checksumFilePath = await _downloadService.DownloadAsync(
                update.ChecksumAsset,
                _updateTempDirectory,
                new Progress<double>(),
                cancellationToken);

            string checksumText = await File.ReadAllTextAsync(checksumFilePath, cancellationToken);
            string? expectedHash = ParseHashFromChecksumFile(checksumText, targetAsset.Name);

            if (!string.IsNullOrWhiteSpace(expectedHash))
            {
                await IntegrityVerificationService.VerifyPublisherChecksumAsync(
                    downloadedFilePath,
                    expectedHash,
                    cancellationToken);
            }
        }

        return downloadedFilePath;
    }

    public static void ApplyUpdateAndRestart(string updateFilePath, bool silent = false, bool exitCurrentProcess = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(updateFilePath);
        if (!File.Exists(updateFilePath))
        {
            throw new FileNotFoundException("Update installer file not found.", updateFilePath);
        }

        string fileName = Path.GetFileName(updateFilePath);
        bool isSetup = fileName.Contains("setup", StringComparison.OrdinalIgnoreCase) ||
                       updateFilePath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);

        string? currentExe = Environment.ProcessPath;
        string fallbackInstallExe = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "GitHub Auto Installer",
            "GitHubAutoInstaller.exe");
        string restartExe = !string.IsNullOrEmpty(currentExe) && File.Exists(currentExe)
            ? currentExe
            : fallbackInstallExe;

        if (isSetup)
        {
            // Run setup with wait, then relaunch the updated executable
            string script = silent
                ? $"/c start /wait \"\" \"{updateFilePath}\" /SILENT /CLOSEAPPLICATIONS & start \"\" \"{restartExe}\""
                : $"/c start /wait \"\" \"{updateFilePath}\" & start \"\" \"{restartExe}\"";

            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = script,
                CreateNoWindow = true,
                UseShellExecute = false
            });
        }
        else
        {
            // Standalone executable update handoff with retry loop to wait for process exit lock release
            if (!string.IsNullOrEmpty(currentExe) && File.Exists(currentExe))
            {
                string script = $"/c :retry & timeout /t 1 /nobreak >nul & copy /y \"{updateFilePath}\" \"{currentExe}\" >nul 2>&1 || goto retry & start \"\" \"{currentExe}\"";
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = script,
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = updateFilePath,
                    UseShellExecute = true
                });
            }
        }

        if (exitCurrentProcess)
        {
            if (Application.Current is not null)
            {
                Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
            }
            else
            {
                Environment.Exit(0);
            }
        }
    }

    public static bool IsNewerVersion(string candidateVersion, string baseVersion)
    {
        string c = CleanVersionString(candidateVersion);
        string b = CleanVersionString(baseVersion);

        if (Version.TryParse(c, out Version? cVer) && Version.TryParse(b, out Version? bVer))
        {
            return cVer > bVer;
        }

        string[] cParts = c.Split('.');
        string[] bParts = b.Split('.');
        int maxLen = Math.Max(cParts.Length, bParts.Length);

        for (int i = 0; i < maxLen; i++)
        {
            int cNum = i < cParts.Length && int.TryParse(cParts[i], out int cn) ? cn : 0;
            int bNum = i < bParts.Length && int.TryParse(bParts[i], out int bn) ? bn : 0;
            if (cNum > bNum) return true;
            if (cNum < bNum) return false;
        }

        return false;
    }

    public static ReleaseAsset? FindBestUpdateAsset(IEnumerable<ReleaseAsset> assets)
    {
        ArgumentNullException.ThrowIfNull(assets);

        return assets
            .Select(asset => new { Asset = asset, Score = ScoreUpdateAsset(asset.Name) })
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Asset.Size)
            .Select(candidate => candidate.Asset)
            .FirstOrDefault();
    }

    public static string? ParseHashFromChecksumFile(string checksumFileContent, string targetFileName)
    {
        if (string.IsNullOrWhiteSpace(checksumFileContent) || string.IsNullOrWhiteSpace(targetFileName))
        {
            return null;
        }

        string targetName = Path.GetFileName(targetFileName).Trim();
        string[] lines = checksumFileContent.Split(["\r\n", "\r", "\n"], StringSplitOptions.RemoveEmptyEntries);

        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith('#')) continue;

            string[] parts = trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                string hash = parts[0];
                string file = parts[1].TrimStart('*');
                if (file.Equals(targetName, StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(file).Equals(targetName, StringComparison.OrdinalIgnoreCase))
                {
                    return hash;
                }
            }
        }

        return null;
    }

    public static int ScoreUpdateAsset(string fileName)
    {
        string name = fileName.ToLowerInvariant();
        string ext = Path.GetExtension(name);

        if (ext != ".exe" && ext != ".msi")
        {
            return -1_000;
        }

        if (ContainsDisallowedArchitecture(name))
        {
            return -1_000;
        }

        int score = 200;

        if (name.Contains("setup", StringComparison.Ordinal) || ext == ".msi")
        {
            score += 300;
        }

        if (name.Contains("x64", StringComparison.Ordinal) || name.Contains("win64", StringComparison.Ordinal) ||
            name.Contains("x86_64", StringComparison.Ordinal) || name.Contains("x86-64", StringComparison.Ordinal))
        {
            score += 100;
        }

        if (name.Contains("win", StringComparison.Ordinal) || name.Contains("windows", StringComparison.Ordinal))
        {
            score += 50;
        }

        return score;
    }

    private static bool ContainsDisallowedArchitecture(string name)
    {
        string normalized = name
            .ToLowerInvariant()
            .Replace("x86_64", "x64", StringComparison.Ordinal)
            .Replace("x86-64", "x64", StringComparison.Ordinal);

        return normalized.Contains("arm64", StringComparison.Ordinal) ||
               normalized.Contains("aarch64", StringComparison.Ordinal) ||
               normalized.Contains("armv7", StringComparison.Ordinal) ||
               normalized.Contains("win32", StringComparison.Ordinal) ||
               normalized.Contains("ia32", StringComparison.Ordinal) ||
               normalized.Contains("x86", StringComparison.Ordinal) ||
               normalized.Contains("linux", StringComparison.Ordinal) ||
               normalized.Contains("darwin", StringComparison.Ordinal) ||
               normalized.Contains("osx", StringComparison.Ordinal);
    }

    private static string CleanVersionString(string ver)
    {
        if (string.IsNullOrWhiteSpace(ver)) return "0.0.0";
        string cleaned = ver.Trim();
        if (cleaned.StartsWith('v') || cleaned.StartsWith('V'))
        {
            cleaned = cleaned[1..];
        }
        int dashIndex = cleaned.IndexOf('-');
        if (dashIndex > 0)
        {
            cleaned = cleaned[..dashIndex];
        }
        return cleaned;
    }

    private static string GetApplicationVersion()
    {
        Version? ver = Assembly.GetExecutingAssembly().GetName().Version;
        if (ver is not null && ver.Major > 0)
        {
            return $"{ver.Major}.{ver.Minor}.{ver.Build}";
        }
        return "1.3.0";
    }
}
