using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using GitHubAutoInstaller.Models;
using GitHubAutoInstaller.Services;

namespace GitHubAutoInstaller;

public partial class MainWindow : Window
{
    public enum ModalKind
    {
        Success,
        Info,
        Warning,
        Error
    }

    public enum ToastKind
    {
        Success,
        Info,
        Warning,
        Error
    }

    private enum WorkflowState
    {
        Idle,
        Inspecting,
        PlanReady,
        Installing,
        Completed,
        Failed
    }

    private static readonly HttpClient Http = new()
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private readonly string _downloadDirectory;
    private readonly GitHubReleaseService _githubService;
    private readonly FileDownloadService _downloadService;
    private readonly AssetInstallerService _installerService;
    private readonly ToolVerificationService _toolVerifier;
    private readonly InstallationEngine _installationEngine;
    private readonly AutoUpdateService _autoUpdateService;
    private UpdateInfo? _latestUpdateInfo;
    private System.Windows.Threading.DispatcherTimer? _updateCheckTimer;
    private string? _downloadedUpdatePath;
    private CancellationTokenSource? _updateDownloadCts;

    private TaskCompletionSource<bool>? _modalTcs;
    private System.Windows.Threading.DispatcherTimer? _toastTimer;
    private Storyboard? _pulseAnimation;

    private WorkflowState _state = WorkflowState.Idle;
    private CancellationTokenSource? _operationCancellation;
    private bool _closeAfterCancellation;

    private GitHubRepository? _currentRepository;
    private RepositoryMetadata? _currentMetadata;
    private GitHubRelease? _currentRelease;
    private RepositoryClassification? _currentClassification;
    private IReadOnlyList<InstallationOption> _availableOptions = [];
    private InstallationOption? _selectedOption;

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
        _toolVerifier = new ToolVerificationService();
        _installationEngine = new InstallationEngine(_downloadService, _installerService, _toolVerifier);
        _autoUpdateService = new AutoUpdateService(Http, _githubService, _downloadService);

        Loaded += MainWindow_Loaded;
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_operationCancellation is not null && !_closeAfterCancellation)
        {
            e.Cancel = true;
            bool shouldExit = await ShowModalAsync(
                "Operation Active",
                "An installation or inspection operation is currently active. Do you want to cancel the operation and close the application?",
                ModalKind.Warning,
                primaryButtonText: "Cancel & Close",
                secondaryButtonText: "Keep Running");

            if (shouldExit)
            {
                _closeAfterCancellation = true;
                _operationCancellation.Cancel();
                Close();
            }

            return;
        }

        _updateCheckTimer?.Stop();
        _updateDownloadCts?.Cancel();
        _updateDownloadCts?.Dispose();

        base.OnClosing(e);
    }

    private void PasteButton_Click(object sender, RoutedEventArgs e)
    {
        if (!Clipboard.ContainsText())
        {
            ShowToast("Clipboard is empty.", ToastKind.Info);
            return;
        }

        string text = Clipboard.GetText().Trim();

        try
        {
            GitHubRepositoryParser.Parse(text);
            UrlBox.Text = text;
            ShowToast("Repository URL pasted.", ToastKind.Success);
        }
        catch (ArgumentException exception)
        {
            ShowToast(exception.Message, ToastKind.Warning);
        }
    }

    private void UrlBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_state is WorkflowState.PlanReady or WorkflowState.Completed or WorkflowState.Failed)
        {
            ResetToIdleState();
        }
    }

    private void UrlBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter && GoButton.IsEnabled)
        {
            e.Handled = true;
            GoButton_Click(GoButton, new RoutedEventArgs());
        }
    }

    private async void GoButton_Click(object sender, RoutedEventArgs e)
    {
        if (_state == WorkflowState.Installing)
        {
            _operationCancellation?.Cancel();
            return;
        }

        if (_state == WorkflowState.Completed)
        {
            ResetToIdleState();
            return;
        }

        if (_state == WorkflowState.PlanReady && _selectedOption is not null)
        {
            await ConfirmAndExecutePlanAsync();
            return;
        }

        await InspectRepositoryAsync();
    }

    private async Task InspectRepositoryAsync()
    {
        using CancellationTokenSource cancellation = new();
        _operationCancellation = cancellation;
        SetBusyState(isBusy: true, statusText: "INSPECTING");
        LogBox.Clear();

        try
        {
            SetStep("Validating GitHub repository URL...", 5);
            _currentRepository = GitHubRepositoryParser.Parse(UrlBox.Text);
            Log($"Inspecting repository: {_currentRepository.FullName}");

            SetStep("Loading repository metadata, releases, and manifests...", 15);
            Task<RepositoryMetadata> metadataTask = _githubService.GetRepositoryAsync(_currentRepository, cancellation.Token);
            Task<GitHubRelease?> releaseTask = _githubService.GetLatestReleaseOrNullAsync(_currentRepository, cancellation.Token);
            Task<IReadOnlyList<string>> rootFilesTask = _githubService.GetRepositoryRootFilesAsync(_currentRepository, cancellation.Token);

            await Task.WhenAll(metadataTask, releaseTask, rootFilesTask);

            _currentMetadata = await metadataTask;
            _currentRelease = await releaseTask;
            IReadOnlyList<string> rootFiles = await rootFilesTask;

            SetStep("Classifying ecosystem and Windows compatibility...", 25);
            _currentClassification = RepositoryInspectorService.Classify(
                _currentRepository,
                _currentRelease,
                rootFiles);

            UpdateRepositoryCard(_currentMetadata, _currentRelease, _currentClassification);

            SetStep("Evaluating dependencies and generating installation plans...", 35);
            string version = _currentRelease?.TagName ?? "latest";
            _availableOptions = await InstallationPlanService.GenerateOptionsAsync(
                _currentRepository,
                version,
                _currentRelease,
                rootFiles,
                _toolVerifier,
                cancellation.Token);

            PopulateMethods(_availableOptions);

            _selectedOption = _availableOptions.FirstOrDefault(opt => opt.IsRecommended)
                ?? _availableOptions.FirstOrDefault();

            if (_selectedOption is not null)
            {
                DisplayPlanDetails(_selectedOption.Plan);
            }

            if (_selectedOption is not null && _selectedOption.Plan.CanExecuteAutomatically)
            {
                _state = WorkflowState.PlanReady;
                SetStep("Plan ready. Review details and click Confirm & Install.", 40);
                UpdateStatus("PLAN READY", 40);
                GoButton.Content = "Confirm & Install";
            }
            else
            {
                _state = WorkflowState.Idle;
                string reason = _selectedOption?.Plan.BlockedReason
                    ?? _currentClassification.UnsupportedReason
                    ?? "Automated installation is not available for this repository.";
                SetStep("Inspection complete: " + reason, 40);
                UpdateStatus("MANUAL REQUIRED", 40);
                GoButton.Content = "Inspect Repository";
            }
        }
        catch (OperationCanceledException)
        {
            SetStep("Inspection cancelled.", 0);
            UpdateStatus("CANCELLED", 0);
        }
        catch (Exception exception)
        {
            SetStep("Inspection failed: " + exception.Message, 0);
            UpdateStatus("ERROR", 0);
            Log("ERROR: " + exception.Message);
            _ = ShowModalAsync(
                "Inspection Failed",
                exception.Message,
                ModalKind.Error);
        }
        finally
        {
            _operationCancellation = null;
            SetBusyState(isBusy: false, statusText: null);
        }
    }

    private async Task ConfirmAndExecutePlanAsync()
    {
        if (_selectedOption is null) return;
        InstallationPlan plan = _selectedOption.Plan;

        // Note: The pre-download confirmation warning modal has been removed per user requirement.
        using CancellationTokenSource cancellation = new();
        _operationCancellation = cancellation;
        _state = WorkflowState.Installing;
        SetBusyState(isBusy: true, statusText: "INSTALLING");
        GoButton.Content = "Cancel";

        System.Windows.Threading.DispatcherTimer? installDurationTimer = null;
        Stopwatch installStopwatch = new();

        try
        {
            SetStep("Starting installation workflow...", 5);

            long totalAssetBytes = plan.TargetAsset?.Size ?? 0;

            Progress<double> progress = new(value =>
            {
                int downloadPct = (int)Math.Round(value * 100);
                int overallPct = (int)Math.Round(value * 50); // 0% to 50% for download phase

                string sizeDetail = string.Empty;
                if (totalAssetBytes > 0)
                {
                    long currentBytes = (long)(value * totalAssetBytes);
                    sizeDetail = $" · {FormatBytes(currentBytes)} / {FormatBytes(totalAssetBytes)}";
                }

                SetStep($"Downloading {plan.TargetAsset?.Name ?? "asset"} ({downloadPct}%){sizeDetail}", overallPct, writeLog: false);
            });

            // Action to wrap logging and launch live installation ticker when adapter starts executing
            Action<string> logWrapper = msg =>
            {
                Log(msg);

                // Detect when adapter begins installation execution
                if (msg.Contains("Starting installation via", StringComparison.OrdinalIgnoreCase) ||
                    msg.Contains("Installing ", StringComparison.OrdinalIgnoreCase) ||
                    msg.Contains("Executing: ", StringComparison.OrdinalIgnoreCase))
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (installDurationTimer is null)
                        {
                            installStopwatch.Restart();
                            string targetName = plan.TargetAsset?.Name ?? plan.Repository.Name;

                            installDurationTimer = new System.Windows.Threading.DispatcherTimer
                            {
                                Interval = TimeSpan.FromMilliseconds(150)
                            };

                            installDurationTimer.Tick += (_, _) =>
                            {
                                TimeSpan elapsed = installStopwatch.Elapsed;
                                // Smooth asymptotic progress from 50% up to 92% based on elapsed duration
                                double factor = 1.0 - Math.Exp(-elapsed.TotalSeconds / 20.0);
                                int currentPercent = 50 + (int)Math.Round(factor * 42.0); // 50% to 92%

                                string timeStr = $"{(int)elapsed.TotalMinutes:D2}:{elapsed.Seconds:D2}";
                                SetStep($"Installing {targetName}... ({timeStr})", currentPercent, writeLog: false);
                            };

                            installDurationTimer.Start();
                        }
                    });
                }
            };

            if (plan.TargetAsset is not null)
            {
                SetStep($"Connecting to GitHub for {plan.TargetAsset.Name}...", 10);
            }
            else
            {
                SetStep("Preparing installation environment...", 20);
            }

            InstallationResult result = await _installationEngine.ExecutePlanAsync(
                plan,
                _downloadDirectory,
                SilentCheck.IsChecked == true,
                progress,
                logWrapper,
                cancellation.Token);

            installDurationTimer?.Stop();
            installDurationTimer = null;

            SetStep("Verifying installation result...", 95);
            if (result.ExitCode is 1_641 or 3_010)
            {
                Log("Installer completed successfully; Windows restart is required.");
            }

            if (ShortcutCheck.IsChecked == true && result.InstalledDirectory is not null)
            {
                string? shortcut = DesktopShortcutService.CreateForBestExecutable(
                    result.InstalledDirectory,
                    plan.Repository.Name);
                Log(shortcut is null
                    ? "No suitable executable was found for a desktop shortcut."
                    : "Desktop shortcut created: " + shortcut);
            }

            SetStep("Installation completed successfully.", 100);
            UpdateStatus("COMPLETE", 100);
            _state = WorkflowState.Completed;
            GoButton.Content = "Inspect Another";

            if (NotifyCheck.IsChecked == true)
            {
                await ShowModalAsync(
                    "Installation Complete",
                    $"{plan.Repository.Name} installed and verified successfully.",
                    ModalKind.Success);
            }
        }
        catch (OperationCanceledException)
        {
            installDurationTimer?.Stop();
            SetStep("Installation cancelled.", 0);
            UpdateStatus("CANCELLED", 0);
            _state = WorkflowState.Idle;
            GoButton.Content = "Inspect Repository";
        }
        catch (Exception exception)
        {
            installDurationTimer?.Stop();
            SetStep("Installation failed: " + exception.Message, 0);
            UpdateStatus("ERROR", 0);
            Log("ERROR: " + exception.Message);
            _state = WorkflowState.Failed;
            GoButton.Content = "Inspect Repository";
            await ShowModalAsync(
                "Installation Failed",
                exception.Message,
                ModalKind.Error);
        }
        finally
        {
            installDurationTimer?.Stop();
            _operationCancellation = null;
            SetBusyState(isBusy: false, statusText: null);

            if (_closeAfterCancellation)
            {
                Close();
            }
        }
    }

    private void PopulateMethods(IReadOnlyList<InstallationOption> options)
    {
        MethodComboBox.Items.Clear();
        foreach (InstallationOption opt in options)
        {
            string label = opt.IsRecommended
                ? $"{opt.Title} (Recommended)"
                : opt.Title;
            MethodComboBox.Items.Add(new ComboBoxItem { Content = label, Tag = opt });
        }

        if (MethodComboBox.Items.Count > 0)
        {
            MethodComboBox.SelectedIndex = 0;
            PlanSection.Visibility = Visibility.Visible;
        }
        else
        {
            PlanSection.Visibility = Visibility.Collapsed;
        }
    }

    private void MethodComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MethodComboBox.SelectedItem is ComboBoxItem item && item.Tag is InstallationOption option)
        {
            _selectedOption = option;
            DisplayPlanDetails(option.Plan);

            if (_state is WorkflowState.PlanReady or WorkflowState.Idle)
            {
                if (option.Plan.CanExecuteAutomatically)
                {
                    _state = WorkflowState.PlanReady;
                    GoButton.IsEnabled = true;
                    GoButton.Content = "Confirm & Install";
                    SetStep("Plan ready. Review details and click Confirm & Install.", 40);
                    UpdateStatus("PLAN READY", 40);
                }
                else
                {
                    _state = WorkflowState.Idle;
                    GoButton.IsEnabled = false;
                    GoButton.Content = "Inspect Repository";
                    string reason = option.Plan.BlockedReason ?? "Automated execution is guarded or unsupported for this installation plan.";
                    SetStep("Selected method: " + reason, 40);
                    UpdateStatus("MANUAL REQUIRED", 40);
                }
            }
        }
    }

    private void DisplayPlanDetails(InstallationPlan plan)
    {
        PlanTitle.Text = plan.DisplayTitle;
        PlanSummary.Text = plan.Summary;
        PermissionsText.Text = plan.RequiredPermissions == RequiredPermissionLevel.RequiresExplicitElevation
            ? "Requires Elevation"
            : "Standard User";

        string commands = plan.ProposedCommands.Count > 0
            ? string.Join("\r\n", plan.ProposedCommands.Select(c => c.DisplayCommand))
            : "None";
        ProposedCommandBox.Text = commands;

        string tools = plan.RequiredTools.Count > 0
            ? string.Join(", ", plan.RequiredTools.Select(t => $"{t.ToolName} ({(t.IsInstalled ? "Installed" : "Missing")})"))
            : "None required";
        PlanToolsText.Text = tools;

        PlanVerificationText.Text = plan.VerificationMethod.Description;
        PlanDestinationText.Text = plan.InstallationDestination ?? "Managed by installer";

        if (plan.SecurityWarnings.Count > 0)
        {
            WarningBanner.Visibility = Visibility.Visible;
            WarningText.Text = "SECURITY NOTE:\n" + string.Join("\n", plan.SecurityWarnings);
        }
        else
        {
            WarningBanner.Visibility = Visibility.Collapsed;
        }

        if (!plan.CanExecuteAutomatically && !string.IsNullOrWhiteSpace(plan.BlockedReason))
        {
            UnsupportedBanner.Visibility = Visibility.Visible;
            UnsupportedText.Text = "ACTION REQUIRED:\n" + plan.BlockedReason;
        }
        else
        {
            UnsupportedBanner.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateRepositoryCard(
        RepositoryMetadata metadata,
        GitHubRelease? release,
        RepositoryClassification classification)
    {
        RepoTitle.Text = metadata.FullName;
        RepoDescription.Text = metadata.Description;
        RepoVersion.Text = release?.TagName ?? "Source Only";
        RepoStars.Text = metadata.Stars.ToString("N0");

        ReleaseAsset? bestAsset = null;
        if (release is not null && release.Assets.Count > 0)
        {
            try { bestAsset = ReleaseAssetSelector.SelectBestWindowsX64Asset(release.Assets); }
            catch { }
        }

        RepoAsset.Text = bestAsset?.Name ?? (release is not null ? "No compatible asset" : "No release published");

        CategoryBadge.Visibility = Visibility.Visible;
        CategoryText.Text = classification.CategoryDescription;

        if (metadata.AvatarUrl is not null)
        {
            RepoAvatar.Source = new BitmapImage(metadata.AvatarUrl);
        }
    }

    private void ResetToIdleState()
    {
        _state = WorkflowState.Idle;
        PlanSection.Visibility = Visibility.Collapsed;
        CategoryBadge.Visibility = Visibility.Collapsed;
        GoButton.Content = "Inspect Repository";
        GoButton.IsEnabled = true;
        SetStep("Ready for repository URL.", 0, writeLog: false);
        UpdateStatus("READY", 0);
    }

    private void SetBusyState(bool isBusy, string? statusText)
    {
        PasteButton.IsEnabled = !isBusy;
        UrlBox.IsEnabled = !isBusy;
        SilentCheck.IsEnabled = !isBusy;
        ShortcutCheck.IsEnabled = !isBusy;
        NotifyCheck.IsEnabled = !isBusy;
        MethodComboBox.IsEnabled = !isBusy;

        if (statusText is not null)
        {
            StatusText.Text = statusText;
        }
    }

    private void SetStep(string text, int percent, bool writeLog = true)
    {
        StepText.Text = text;
        PercentText.Text = percent + "%";
        AnimateProgress(percent);

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
            ShowToast("Activity log copied to clipboard.", ToastKind.Success);
        }
    }

    private void ClearLogButton_Click(object sender, RoutedEventArgs e)
    {
        LogBox.Clear();
    }

    private void UpdateStatus(string status, int percent)
    {
        string brushKey;
        Color dotColor;

        if (status.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
        {
            brushKey = "StatusErrorBrush";
            dotColor = Color.FromRgb(254, 202, 202);
            StopStatusPulsing();
        }
        else if (status.StartsWith("CANCELLED", StringComparison.OrdinalIgnoreCase))
        {
            brushKey = "StatusWarningBrush";
            dotColor = Color.FromRgb(254, 240, 138);
            StopStatusPulsing();
        }
        else if (status.StartsWith("MANUAL", StringComparison.OrdinalIgnoreCase))
        {
            brushKey = "StatusWarningBrush";
            dotColor = Color.FromRgb(254, 240, 138);
            StopStatusPulsing();
        }
        else if (percent >= 100 || status.StartsWith("COMPLETE", StringComparison.OrdinalIgnoreCase))
        {
            brushKey = "StatusSuccessBrush";
            dotColor = Color.FromRgb(167, 243, 208);
            StopStatusPulsing();
        }
        else if (status.StartsWith("INSTALL", StringComparison.OrdinalIgnoreCase) ||
                 status.StartsWith("INSPECT", StringComparison.OrdinalIgnoreCase) ||
                 status.StartsWith("UPDAT", StringComparison.OrdinalIgnoreCase) ||
                 status.StartsWith("RESTART", StringComparison.OrdinalIgnoreCase))
        {
            brushKey = "StatusWorkingBrush";
            dotColor = Color.FromRgb(191, 219, 254);
            StartStatusPulsing();
        }
        else if (percent > 0 || status.StartsWith("PLAN", StringComparison.OrdinalIgnoreCase))
        {
            brushKey = "StatusWorkingBrush";
            dotColor = Color.FromRgb(191, 219, 254);
            StopStatusPulsing();
        }
        else
        {
            brushKey = "StatusReadyBrush";
            dotColor = Color.FromRgb(203, 213, 225);
            StopStatusPulsing();
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

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _ = CheckForUpdatesSilentlyAsync();

        _updateCheckTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(30)
        };
        _updateCheckTimer.Tick += async (_, _) => await CheckForUpdatesSilentlyAsync();
        _updateCheckTimer.Start();
    }

    private async Task CheckForUpdatesSilentlyAsync()
    {
        try
        {
            UpdateInfo update = await _autoUpdateService.CheckForUpdatesAsync();
            if (update.IsUpdateAvailable)
            {
                _latestUpdateInfo = update;
                UpdateBannerTitle.Text = $"Update v{update.LatestVersion} Available";
                UpdateBannerSubtitle.Text = "Receiving update in background...";
                UpdateNowButton.Content = "Receiving...";
                UpdateNowButton.IsEnabled = false;
                UpdateBanner.Visibility = Visibility.Visible;
                Log($"[Auto-Updater] New version v{update.LatestVersion} discovered. Automatically receiving update in background...");

                _updateDownloadCts?.Cancel();
                _updateDownloadCts?.Dispose();
                _updateDownloadCts = new CancellationTokenSource();

                try
                {
                    string downloadedPath = await _autoUpdateService.DownloadAndVerifyUpdateAsync(
                        update,
                        new Progress<double>(p =>
                        {
                            Dispatcher.Invoke(() =>
                            {
                                UpdateBannerSubtitle.Text = $"Receiving update v{update.LatestVersion} ({p:P0})...";
                            });
                        }),
                        _updateDownloadCts.Token);

                    _downloadedUpdatePath = downloadedPath;
                    UpdateBannerTitle.Text = $"Update v{update.LatestVersion} Ready";
                    UpdateBannerSubtitle.Text = $"Version v{update.LatestVersion} received and verified. Click 'Restart to Apply'.";
                    UpdateNowButton.Content = "Restart to Apply";
                    UpdateNowButton.IsEnabled = true;
                    Log($"[Auto-Updater] Update v{update.LatestVersion} received and verified successfully. Ready to apply.");
                }
                catch (OperationCanceledException)
                {
                    // Ignore cancellation on shutdown/close
                }
                catch (Exception ex)
                {
                    Log($"[Auto-Updater] Background download note: {ex.Message}. Manual update available.");
                    UpdateBannerTitle.Text = $"Update v{update.LatestVersion} Available";
                    UpdateBannerSubtitle.Text = $"Click 'Update Now' to download and apply v{update.LatestVersion}.";
                    UpdateNowButton.Content = "Update Now";
                    UpdateNowButton.IsEnabled = true;
                }
            }
        }
        catch (Exception ex)
        {
            Log($"[Auto-Updater] Background check notice: {ex.Message}");
        }
    }

    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        CheckUpdatesButton.Content = "Checking...";
        Log("[Auto-Updater] Checking GitHub for updates...");

        try
        {
            UpdateInfo update = await _autoUpdateService.CheckForUpdatesAsync();
            if (update.IsUpdateAvailable)
            {
                _latestUpdateInfo = update;
                if (!string.IsNullOrEmpty(_downloadedUpdatePath) && File.Exists(_downloadedUpdatePath))
                {
                    UpdateBannerTitle.Text = $"Update v{update.LatestVersion} Ready";
                    UpdateBannerSubtitle.Text = $"Version v{update.LatestVersion} received and verified. Click 'Restart to Apply'.";
                    UpdateNowButton.Content = "Restart to Apply";
                    UpdateNowButton.IsEnabled = true;
                }
                else
                {
                    UpdateBannerTitle.Text = $"New Version Available: v{update.LatestVersion}";
                    UpdateBannerSubtitle.Text = $"A newer version is published on GitHub ({_autoUpdateService.Repository.FullName}). Click 'Update Now' to receive and apply it.";
                    UpdateNowButton.Content = "Update Now";
                    UpdateNowButton.IsEnabled = true;
                }
                UpdateBanner.Visibility = Visibility.Visible;
                Log($"[Auto-Updater] Update available: v{update.LatestVersion} (current: v{update.CurrentVersion}).");
            }
            else
            {
                UpdateBanner.Visibility = Visibility.Collapsed;
                Log($"[Auto-Updater] You are up to date! Current version: v{update.CurrentVersion}.");
                ShowToast($"You are running the latest version of GitHub Auto Installer (v{update.CurrentVersion}).", ToastKind.Success);
            }
        }
        catch (Exception ex)
        {
            Log($"[Auto-Updater] Failed to check for updates: {ex.Message}");
            ShowToast($"Failed to check for updates: {ex.Message}", ToastKind.Warning);
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
            CheckUpdatesButton.Content = "Check updates";
        }
    }

    private async void UpdateNowButton_Click(object sender, RoutedEventArgs e)
    {
        if (_latestUpdateInfo is null || !_latestUpdateInfo.IsUpdateAvailable)
        {
            return;
        }

        // If the update has already been downloaded and verified in the background
        if (!string.IsNullOrEmpty(_downloadedUpdatePath) && File.Exists(_downloadedUpdatePath))
        {
            UpdateNowButton.IsEnabled = false;
            DismissUpdateBannerButton.IsEnabled = false;
            SetBusyState(isBusy: true, statusText: "RESTARTING");
            SetStep($"Applying update v{_latestUpdateInfo.LatestVersion} and restarting...", 95);
            Log($"[Auto-Updater] Applying update: {Path.GetFileName(_downloadedUpdatePath)}");

            AutoUpdateService.ApplyUpdateAndRestart(_downloadedUpdatePath, silent: SilentCheck.IsChecked == true);
            return;
        }

        UpdateNowButton.IsEnabled = false;
        UpdateNowButton.Content = "Updating...";
        DismissUpdateBannerButton.IsEnabled = false;
        SetBusyState(isBusy: true, statusText: "UPDATING");

        try
        {
            SetStep($"Downloading update v{_latestUpdateInfo.LatestVersion}...", 20);
            Log($"[Auto-Updater] Downloading update v{_latestUpdateInfo.LatestVersion}...");

            Progress<double> progress = new(p =>
            {
                int pct = (int)Math.Clamp(20 + (p * 70), 20, 90);
                SetStep($"Downloading update v{_latestUpdateInfo.LatestVersion} ({p:P0})...", pct);
            });

            _updateDownloadCts?.Cancel();
            _updateDownloadCts?.Dispose();
            _updateDownloadCts = new CancellationTokenSource();

            string downloadedPath = await _autoUpdateService.DownloadAndVerifyUpdateAsync(
                _latestUpdateInfo,
                progress,
                _updateDownloadCts.Token);

            _downloadedUpdatePath = downloadedPath;
            SetStep("Update downloaded and verified. Launching update...", 95);
            Log($"[Auto-Updater] Applying update: {Path.GetFileName(downloadedPath)}");

            AutoUpdateService.ApplyUpdateAndRestart(downloadedPath, silent: SilentCheck.IsChecked == true);
        }
        catch (Exception ex)
        {
            SetStep($"Update failed: {ex.Message}", 0);
            UpdateStatus("ERROR", 0);
            Log($"[Auto-Updater] Update error: {ex.Message}");
            _ = ShowModalAsync(
                "Update Failed",
                $"Failed to download or apply update:\n{ex.Message}",
                ModalKind.Error);
            UpdateNowButton.IsEnabled = true;
            UpdateNowButton.Content = "Retry Update";
            DismissUpdateBannerButton.IsEnabled = true;
            SetBusyState(isBusy: false, statusText: null);
        }
    }

    private void ViewReleaseButton_Click(object sender, RoutedEventArgs e)
    {
        Uri url = _latestUpdateInfo?.ReleasePageUrl ?? new Uri($"https://github.com/{_autoUpdateService.Repository.FullName}/releases");
        Process.Start(new ProcessStartInfo
        {
            FileName = url.ToString(),
            UseShellExecute = true
        });
    }

    private void DismissUpdateBannerButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateBanner.Visibility = Visibility.Collapsed;
    }

    private Task<bool> ShowModalAsync(
        string title,
        string message,
        ModalKind kind,
        string primaryButtonText = "OK",
        string? secondaryButtonText = null)
    {
        _modalTcs = new TaskCompletionSource<bool>();

        ModalTitle.Text = title;
        ModalMessage.Text = message;
        ModalPrimaryButton.Content = primaryButtonText;

        if (!string.IsNullOrEmpty(secondaryButtonText))
        {
            ModalSecondaryButton.Content = secondaryButtonText;
            ModalSecondaryButton.Visibility = Visibility.Visible;
        }
        else
        {
            ModalSecondaryButton.Visibility = Visibility.Collapsed;
        }

        ApplyModalTheme(kind);
        ModalOverlay.Visibility = Visibility.Visible;

        return _modalTcs.Task;
    }

    private void ModalPrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        ModalOverlay.Visibility = Visibility.Collapsed;
        _modalTcs?.TrySetResult(true);
    }

    private void ModalSecondaryButton_Click(object sender, RoutedEventArgs e)
    {
        ModalOverlay.Visibility = Visibility.Collapsed;
        _modalTcs?.TrySetResult(false);
    }

    private void ApplyModalTheme(ModalKind kind)
    {
        switch (kind)
        {
            case ModalKind.Success:
                ModalIconText.Text = "✓";
                ModalIconBadge.Background = new SolidColorBrush(Color.FromArgb(40, 16, 185, 129));
                ModalIconBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                ModalIconText.Foreground = new SolidColorBrush(Color.FromRgb(52, 211, 153));
                ModalCardBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                break;
            case ModalKind.Error:
                ModalIconText.Text = "✕";
                ModalIconBadge.Background = new SolidColorBrush(Color.FromArgb(40, 239, 68, 68));
                ModalIconBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                ModalIconText.Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113));
                ModalCardBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                break;
            case ModalKind.Warning:
                ModalIconText.Text = "⚠";
                ModalIconBadge.Background = new SolidColorBrush(Color.FromArgb(40, 245, 158, 11));
                ModalIconBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(245, 158, 11));
                ModalIconText.Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36));
                ModalCardBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(245, 158, 11));
                break;
            case ModalKind.Info:
            default:
                ModalIconText.Text = "ℹ";
                ModalIconBadge.Background = new SolidColorBrush(Color.FromArgb(40, 59, 130, 246));
                ModalIconBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(59, 130, 246));
                ModalIconText.Foreground = new SolidColorBrush(Color.FromRgb(96, 165, 250));
                ModalCardBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(59, 130, 246));
                break;
        }
    }

    private void ShowToast(string message, ToastKind kind = ToastKind.Info)
    {
        ToastMessage.Text = message;
        ApplyToastTheme(kind);
        ToastCard.Visibility = Visibility.Visible;

        _toastTimer?.Stop();
        _toastTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3.5)
        };
        _toastTimer.Tick += (_, _) =>
        {
            ToastCard.Visibility = Visibility.Collapsed;
            _toastTimer.Stop();
        };
        _toastTimer.Start();
    }

    private void ToastCloseButton_Click(object sender, RoutedEventArgs e)
    {
        ToastCard.Visibility = Visibility.Collapsed;
        _toastTimer?.Stop();
    }

    private void ApplyToastTheme(ToastKind kind)
    {
        switch (kind)
        {
            case ToastKind.Success:
                ToastIconText.Text = "✓";
                ToastIconText.Foreground = new SolidColorBrush(Color.FromRgb(52, 211, 153));
                ToastCard.BorderBrush = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                break;
            case ToastKind.Warning:
                ToastIconText.Text = "⚠";
                ToastIconText.Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36));
                ToastCard.BorderBrush = new SolidColorBrush(Color.FromRgb(245, 158, 11));
                break;
            case ToastKind.Error:
                ToastIconText.Text = "✕";
                ToastIconText.Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113));
                ToastCard.BorderBrush = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                break;
            case ToastKind.Info:
            default:
                ToastIconText.Text = "ℹ";
                ToastIconText.Foreground = new SolidColorBrush(Color.FromRgb(96, 165, 250));
                ToastCard.BorderBrush = new SolidColorBrush(Color.FromRgb(59, 130, 246));
                break;
        }
    }

    private void StartStatusPulsing()
    {
        if (_pulseAnimation is not null) return;

        DoubleAnimation opacityAnim = new()
        {
            From = 1.0,
            To = 0.35,
            Duration = TimeSpan.FromMilliseconds(700),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };

        _pulseAnimation = new Storyboard();
        _pulseAnimation.Children.Add(opacityAnim);
        Storyboard.SetTarget(_pulseAnimation, StatusDot);
        Storyboard.SetTargetProperty(_pulseAnimation, new PropertyPath(UIElement.OpacityProperty));
        _pulseAnimation.Begin();
    }

    private void StopStatusPulsing()
    {
        if (_pulseAnimation is not null)
        {
            _pulseAnimation.Stop();
            _pulseAnimation = null;
            StatusDot.Opacity = 1.0;
        }
    }

    private void AnimateProgress(double targetValue)
    {
        DoubleAnimation anim = new()
        {
            To = targetValue,
            Duration = TimeSpan.FromMilliseconds(250),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        ProgressBar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, anim);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
            return $"{bytes / (1024d * 1024 * 1024):0.##} GB";
        if (bytes >= 1024L * 1024)
            return $"{bytes / (1024d * 1024):0.#} MB";
        if (bytes >= 1024L)
            return $"{bytes / 1024d:0.#} KB";
        return $"{bytes} B";
    }
}
