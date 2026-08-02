using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GitHubAutoInstaller.Models;
using GitHubAutoInstaller.Services;

namespace GitHubAutoInstaller;

public partial class MainWindow : Window
{
    private static readonly HttpClient Http = new()
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private readonly string _downloadDirectory;
    private readonly GitHubReleaseService _githubService;
    private readonly FileDownloadService _downloadService;
    private readonly AssetInstallerService _installerService;
    private CancellationTokenSource? _operationCancellation;
    private bool _closeAfterCancellation;

    public MainWindow()
    {
        InitializeComponent();

        string appRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GitHubAutoInstaller");
        _downloadDirectory = Path.Combine(appRoot, "Downloads");
        string installRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GitHubTools");

        Directory.CreateDirectory(_downloadDirectory);
        _githubService = new GitHubReleaseService(Http);
        _downloadService = new FileDownloadService(Http);
        _installerService = new AssetInstallerService(installRoot);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_operationCancellation is not null && !_closeAfterCancellation)
        {
            e.Cancel = true;
            MessageBoxResult result = MessageBox.Show(
                "An installation is active. Cancel it and close the application?",
                "Installation Active",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                _closeAfterCancellation = true;
                _operationCancellation.Cancel();
            }

            return;
        }

        base.OnClosing(e);
    }

    private void PasteButton_Click(object sender, RoutedEventArgs e)
    {
        if (!Clipboard.ContainsText())
        {
            MessageBox.Show(
                "Clipboard is empty.",
                "Paste",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        string text = Clipboard.GetText().Trim();

        try
        {
            GitHubRepositoryParser.Parse(text);
            UrlBox.Text = text;
        }
        catch (ArgumentException exception)
        {
            MessageBox.Show(
                exception.Message,
                "Invalid Clipboard",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async void GoButton_Click(object sender, RoutedEventArgs e)
    {
        if (_operationCancellation is not null)
        {
            _operationCancellation.Cancel();
            return;
        }

        using CancellationTokenSource cancellation = new();
        _operationCancellation = cancellation;
        SetBusyState(isBusy: true);
        LogBox.Clear();

        try
        {
            await RunInstallerAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            SetStep("Cancelled.", 0);
        }
        catch (Exception exception)
        {
            SetStep("Failed.", 0);
            Log("ERROR: " + exception.Message);
            _operationCancellation = null;
            SetBusyState(isBusy: false);
            MessageBox.Show(
                exception.Message,
                "Installation Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _operationCancellation = null;
            SetBusyState(isBusy: false);

            if (_closeAfterCancellation)
            {
                Close();
            }
        }
    }

    private async Task RunInstallerAsync(CancellationToken cancellationToken)
    {
        SetStep("Validating GitHub URL...", 5);
        GitHubRepository repository = GitHubRepositoryParser.Parse(UrlBox.Text);
        Log($"Repository detected: {repository.FullName}");

        SetStep("Loading repository and latest release...", 20);
        Task<RepositoryMetadata> metadataTask = _githubService.GetRepositoryAsync(
            repository,
            cancellationToken);
        Task<GitHubRelease> releaseTask = _githubService.GetLatestReleaseAsync(
            repository,
            cancellationToken);
        await Task.WhenAll(metadataTask, releaseTask);

        RepositoryMetadata metadata = await metadataTask;
        GitHubRelease release = await releaseTask;
        ReleaseAsset asset = ReleaseAssetSelector.SelectBestWindowsX64Asset(release.Assets);
        UpdateRepositoryCard(metadata, release, asset);

        Log($"Latest release: {release.TagName}");
        Log($"Selected asset: {asset.Name} ({FormatBytes(asset.Size)})");
        SetStep("Asset inspected. Waiting for confirmation...", 40);

        MessageBoxResult confirmation = MessageBox.Show(
            $"Install {asset.Name}?\n\nRelease: {release.TagName}\nSize: {FormatBytes(asset.Size)}",
            "Confirm Installation",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirmation != MessageBoxResult.Yes)
        {
            throw new OperationCanceledException("Installation was cancelled before download.");
        }

        SetStep("Downloading asset...", 50);

        Progress<double> downloadProgress = new(value =>
        {
            int percentage = 50 + (int)Math.Round(value * 20);
            SetStep($"Downloading asset... {(int)Math.Round(value * 100)}%", percentage, writeLog: false);
        });

        string downloadPath = await _downloadService.DownloadAsync(
            asset,
            _downloadDirectory,
            downloadProgress,
            cancellationToken);
        Log("Downloaded: " + downloadPath);
        Log("SHA-256: " + await ComputeSha256Async(downloadPath, cancellationToken));

        SetStep("Installing or extracting...", 75);
        InstallationResult result = await _installerService.InstallAsync(
            downloadPath,
            repository.Name,
            SilentCheck.IsChecked == true,
            Log,
            cancellationToken);

        if (result.ExitCode is 1_641 or 3_010)
        {
            Log("Installer completed successfully; Windows restart is required.");
        }

        SetStep("Finalizing...", 90);
        if (ShortcutCheck.IsChecked == true && result.InstalledDirectory is not null)
        {
            string? shortcut = DesktopShortcutService.CreateForBestExecutable(
                result.InstalledDirectory,
                repository.Name);
            Log(shortcut is null
                ? "No suitable executable was found for a desktop shortcut."
                : "Desktop shortcut created: " + shortcut);
        }
        else if (ShortcutCheck.IsChecked == true)
        {
            Log("Package installer controls desktop shortcut creation.");
        }

        SetStep("Completed successfully.", 100);
        if (NotifyCheck.IsChecked == true)
        {
            _operationCancellation = null;
            SetBusyState(isBusy: false);
            MessageBox.Show(
                $"{repository.Name} installed successfully.",
                "Completed",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private void UpdateRepositoryCard(
        RepositoryMetadata metadata,
        GitHubRelease release,
        ReleaseAsset asset)
    {
        RepoTitle.Text = metadata.FullName;
        RepoDescription.Text = metadata.Description;
        RepoVersion.Text = release.TagName;
        RepoStars.Text = metadata.Stars.ToString("N0");
        RepoAsset.Text = asset.Name;

        if (metadata.AvatarUrl is not null)
        {
            RepoAvatar.Source = new BitmapImage(metadata.AvatarUrl);
        }
    }

    private void SetBusyState(bool isBusy)
    {
        PasteButton.IsEnabled = !isBusy;
        UrlBox.IsEnabled = !isBusy;
        SilentCheck.IsEnabled = !isBusy;
        ShortcutCheck.IsEnabled = !isBusy;
        NotifyCheck.IsEnabled = !isBusy;
        GoButton.Content = isBusy ? "Cancel" : "Inspect & Install";
    }

    private void SetStep(string text, int percent, bool writeLog = true)
    {
        StepText.Text = text;
        ProgressBar.Value = percent;
        PercentText.Text = percent + "%";
        UpdateStatus(text, percent);

        if (writeLog)
        {
            Log(text);
        }
    }

    private void OpenDownloadsButton_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_downloadDirectory);
        Process.Start(new ProcessStartInfo
        {
            FileName = _downloadDirectory,
            UseShellExecute = true
        });
    }

    private void CopyLogButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(LogBox.Text))
        {
            Clipboard.SetText(LogBox.Text);
        }
    }

    private void ClearLogButton_Click(object sender, RoutedEventArgs e)
    {
        LogBox.Clear();
    }

    private void UpdateStatus(string step, int percent)
    {
        string status;
        string brushKey;
        Color dotColor;

        if (step.StartsWith("Failed", StringComparison.OrdinalIgnoreCase))
        {
            status = "ERROR";
            brushKey = "StatusErrorBrush";
            dotColor = Color.FromRgb(254, 202, 202);
        }
        else if (step.StartsWith("Cancelled", StringComparison.OrdinalIgnoreCase))
        {
            status = "CANCELLED";
            brushKey = "StatusWarningBrush";
            dotColor = Color.FromRgb(254, 240, 138);
        }
        else if (percent >= 100)
        {
            status = "COMPLETE";
            brushKey = "StatusSuccessBrush";
            dotColor = Color.FromRgb(167, 243, 208);
        }
        else if (percent > 0)
        {
            status = "WORKING";
            brushKey = "StatusWorkingBrush";
            dotColor = Color.FromRgb(191, 219, 254);
        }
        else
        {
            status = "READY";
            brushKey = "StatusReadyBrush";
            dotColor = Color.FromRgb(203, 213, 225);
        }

        StatusText.Text = status;
        StatusBadge.Background = (Brush)FindResource(brushKey);
        StatusDot.Fill = new SolidColorBrush(dotColor);
    }

    private void Log(string text)
    {
        Dispatcher.Invoke(() =>
        {
            LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}\r\n");
            LogBox.ScrollToEnd();
        });
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "unknown size";
        }

        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1_024 && unit < units.Length - 1)
        {
            value /= 1_024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }

    private static async Task<string> ComputeSha256Async(
        string filePath,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81_920,
            useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }
}
