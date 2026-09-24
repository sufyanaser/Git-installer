using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GitHubAutoInstaller.Models;
using GitHubAutoInstaller.Services;

namespace GitHubAutoInstaller;

public partial class MainWindow : Window
{
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
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_operationCancellation is not null && !_closeAfterCancellation)
        {
            e.Cancel = true;
            MessageBoxResult result = MessageBox.Show(
                "An operation is active. Cancel it and close the application?",
                "Operation Active",
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

    private void UrlBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_state is WorkflowState.PlanReady or WorkflowState.Completed or WorkflowState.Failed)
        {
            ResetToIdleState();
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
            MessageBox.Show(
                exception.Message,
                "Inspection Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
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

        // Explicit User Confirmation Boundary
        string warningDetail = plan.SecurityWarnings.Count > 0
            ? "\n\nSecurity Warnings:\n- " + string.Join("\n- ", plan.SecurityWarnings)
            : string.Empty;

        string proposedCommandsDetail = plan.ProposedCommands.Count > 0
            ? "\n\nProposed Command(s):\n" + string.Join("\n", plan.ProposedCommands.Select(c => c.DisplayCommand))
            : string.Empty;

        string confirmationMessage =
            $"Authorize installation of {plan.Repository.FullName} ({plan.Version})?\n\n" +
            $"Method: {plan.DisplayTitle}\n" +
            $"Permissions: {plan.RequiredPermissions}\n" +
            $"Destination: {plan.InstallationDestination}" +
            proposedCommandsDetail +
            warningDetail +
            "\n\nDo you want to proceed with this installation plan?";

        MessageBoxResult confirmation = MessageBox.Show(
            confirmationMessage,
            "Confirm Installation Plan",
            MessageBoxButton.YesNo,
            plan.ExecutesPublisherCode ? MessageBoxImage.Warning : MessageBoxImage.Question,
            MessageBoxResult.No);

        if (confirmation != MessageBoxResult.Yes)
        {
            Log("Installation cancelled by user before download or execution.");
            SetStep("Installation cancelled by user.", 40);
            UpdateStatus("CANCELLED", 40);
            return;
        }

        using CancellationTokenSource cancellation = new();
        _operationCancellation = cancellation;
        _state = WorkflowState.Installing;
        SetBusyState(isBusy: true, statusText: "INSTALLING");
        GoButton.Content = "Cancel";

        try
        {
            SetStep("Starting installation workflow...", 45);

            Progress<double> progress = new(value =>
            {
                int percentage = 50 + (int)Math.Round(value * 25);
                SetStep($"Downloading asset... {(int)Math.Round(value * 100)}%", percentage, writeLog: false);
            });

            SetStep("Downloading and verifying integrity...", 50);
            InstallationResult result = await _installationEngine.ExecutePlanAsync(
                plan,
                _downloadDirectory,
                SilentCheck.IsChecked == true,
                progress,
                Log,
                cancellation.Token);

            SetStep("Verifying installation result...", 85);
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
                MessageBox.Show(
                    $"{plan.Repository.Name} installed and verified successfully.",
                    "Installation Complete",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (OperationCanceledException)
        {
            SetStep("Installation cancelled.", 0);
            UpdateStatus("CANCELLED", 0);
            _state = WorkflowState.Idle;
            GoButton.Content = "Inspect Repository";
        }
        catch (Exception exception)
        {
            SetStep("Installation failed: " + exception.Message, 0);
            UpdateStatus("ERROR", 0);
            Log("ERROR: " + exception.Message);
            _state = WorkflowState.Failed;
            GoButton.Content = "Inspect Repository";
            MessageBox.Show(
                exception.Message,
                "Installation Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
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

            if (_state == WorkflowState.PlanReady)
            {
                GoButton.IsEnabled = option.Plan.CanExecuteAutomatically;
                GoButton.Content = option.Plan.CanExecuteAutomatically ? "Confirm & Install" : "Inspect Repository";
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
        ProgressBar.Value = percent;
        PercentText.Text = percent + "%";

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

    private void UpdateStatus(string status, int percent)
    {
        string brushKey;
        Color dotColor;

        if (status.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
        {
            brushKey = "StatusErrorBrush";
            dotColor = Color.FromRgb(254, 202, 202);
        }
        else if (status.StartsWith("CANCELLED", StringComparison.OrdinalIgnoreCase))
        {
            brushKey = "StatusWarningBrush";
            dotColor = Color.FromRgb(254, 240, 138);
        }
        else if (status.StartsWith("MANUAL", StringComparison.OrdinalIgnoreCase))
        {
            brushKey = "StatusWarningBrush";
            dotColor = Color.FromRgb(254, 240, 138);
        }
        else if (percent >= 100 || status.StartsWith("COMPLETE", StringComparison.OrdinalIgnoreCase))
        {
            brushKey = "StatusSuccessBrush";
            dotColor = Color.FromRgb(167, 243, 208);
        }
        else if (percent > 0 || status.StartsWith("PLAN", StringComparison.OrdinalIgnoreCase))
        {
            brushKey = "StatusWorkingBrush";
            dotColor = Color.FromRgb(191, 219, 254);
        }
        else
        {
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
}
