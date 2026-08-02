using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace GitHubAutoInstaller;

public partial class MainWindow : Window
{
    private static readonly HttpClient Http = new();

    private readonly string AppRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GitHubAutoInstaller"
    );

    private readonly string DownloadDir;
    private readonly string InstallRoot;

    public MainWindow()
    {
        InitializeComponent();

        DownloadDir = Path.Combine(AppRoot, "Downloads");
        InstallRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GitHubTools"
        );

        Directory.CreateDirectory(DownloadDir);
        Directory.CreateDirectory(InstallRoot);

        Http.DefaultRequestHeaders.UserAgent.ParseAdd("GitHubAutoInstaller/1.0");
    }

    private void PasteButton_Click(object sender, RoutedEventArgs e)
    {
        if (!Clipboard.ContainsText())
        {
            MessageBox.Show("Clipboard is empty.", "Paste", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string text = Clipboard.GetText().Trim();

        if (!text.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("Clipboard does not contain a valid GitHub repository URL.", "Invalid Clipboard", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        UrlBox.Text = text;
    }

    private async void GoButton_Click(object sender, RoutedEventArgs e)
    {
        GoButton.IsEnabled = false;
        PasteButton.IsEnabled = false;
        LogBox.Clear();

        try
        {
            await RunInstaller();
        }
        catch (Exception ex)
        {
            SetStep("Failed.", 0, true);
            Log("ERROR: " + ex.Message);
            MessageBox.Show(ex.Message, "Installation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            GoButton.IsEnabled = true;
            PasteButton.IsEnabled = true;
        }
    }

    private async Task RunInstaller()
    {
        SetStep("Validating GitHub URL...", 5);

        var repo = ParseGitHubUrl(UrlBox.Text.Trim());
        Log($"Repository detected: {repo.Owner}/{repo.Name}");

        SetStep("Checking latest GitHub release...", 20);

        using var releaseJson = await GetLatestRelease(repo.Owner, repo.Name);

        string tag = releaseJson.RootElement.GetProperty("tag_name").GetString() ?? "unknown";
        Log("Latest release: " + tag);

        SetStep("Selecting best Windows x64 asset...", 35);

        var asset = SelectBestAsset(releaseJson.RootElement);
        Log("Selected asset: " + asset.Name);

        SetStep("Downloading asset...", 50);

        string downloadPath = Path.Combine(DownloadDir, SafeFileName(asset.Name));
        await DownloadFile(asset.Url, downloadPath);

        Log("Downloaded: " + downloadPath);

        SetStep("Installing or extracting...", 75);

        bool silentInstall = SilentCheck.IsChecked == true;

        string? installedPath = await Task.Run(() =>
            InstallAsset(downloadPath, repo.Name, silentInstall)
        );

        SetStep("Creating desktop shortcut...", 90);

        bool createShortcut = ShortcutCheck.IsChecked == true;

        if (!string.IsNullOrWhiteSpace(installedPath) && createShortcut)
        {
            CreateDesktopShortcut(installedPath, repo.Name);
        }
        else
        {
            Log("Installer completed. Shortcut may be handled by installer.");
        }

        SetStep("Completed successfully.", 100);
        MessageBox.Show($"{repo.Name} installed successfully.", "Completed", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private (string Owner, string Name) ParseGitHubUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new Exception("GitHub URL is empty.");

        var uri = new Uri(url);

        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            throw new Exception("Invalid GitHub URL.");

        var parts = uri.AbsolutePath.Trim('/').Split('/');

        if (parts.Length < 2)
            throw new Exception("Repository URL must be like: https://github.com/user/repo");

        return (parts[0], parts[1].Replace(".git", ""));
    }

    private async Task<JsonDocument> GetLatestRelease(string owner, string repo)
    {
        string latestUrl = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";

        var response = await Http.GetAsync(latestUrl);

        if (!response.IsSuccessStatusCode)
            throw new Exception("No latest release found for this repository.");

        var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }

    private (string Name, string Url) SelectBestAsset(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets))
            throw new Exception("No release assets found.");

        var candidates = assets.EnumerateArray()
            .Select(asset =>
            {
                string name = asset.GetProperty("name").GetString() ?? "";
                string url = asset.GetProperty("browser_download_url").GetString() ?? "";

                return new
                {
                    Name = name,
                    Url = url,
                    Score = GetAssetScore(name)
                };
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ToList();

        if (candidates.Count == 0)
            throw new Exception("No supported Windows x64 installer found. Supported: .exe / .msi / .zip");

        return (candidates[0].Name, candidates[0].Url);
    }

    private int GetAssetScore(string fileName)
    {
        string name = fileName.ToLowerInvariant();
        int score = 0;

        if (name.EndsWith(".exe")) score += 100;
        if (name.EndsWith(".msi")) score += 95;
        if (name.EndsWith(".zip")) score += 70;

        if (name.Contains("x64") || name.Contains("amd64") || name.Contains("x86_64") || name.Contains("win64"))
            score += 60;

        if (name.Contains("windows") || name.Contains("win"))
            score += 40;

        if (name.Contains("setup") || name.Contains("installer") || name.Contains("install"))
            score += 30;

        if (name.Contains("ia32") || name.Contains("win32") || name.Contains("i386"))
            score -= 500;

        if (name.Contains("arm") || name.Contains("arm64") || name.Contains("aarch64"))
            score -= 500;

        if (name.Contains("linux") || name.Contains("mac") || name.Contains("darwin") || name.Contains("osx"))
            score -= 500;

        if (name.Contains("sha") || name.Contains("checksum") || name.Contains("blockmap") || name.EndsWith(".yml"))
            score -= 500;

        return score;
    }

    private async Task DownloadFile(string url, string outputPath)
    {
        if (File.Exists(outputPath))
            File.Delete(outputPath);

        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        long? total = response.Content.Headers.ContentLength;

        await using var input = await response.Content.ReadAsStreamAsync();
        await using var output = File.Create(outputPath);

        byte[] buffer = new byte[81920];
        long readTotal = 0;
        int read;
        int lastPercent = -1;

        while ((read = await input.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            await output.WriteAsync(buffer, 0, read);
            readTotal += read;

            if (total.HasValue && total.Value > 0)
            {
                int percent = 50 + (int)((readTotal * 20) / total.Value);
                percent = Math.Min(percent, 70);

                if (percent != lastPercent)
                {
                    ProgressBar.Value = percent;
                    PercentText.Text = percent + "%";
                    StepText.Text = $"Downloading asset... {percent}%";
                    lastPercent = percent;
                }
            }
        }

        ProgressBar.Value = 70;
        PercentText.Text = "70%";
        StepText.Text = "Download completed.";
    }

    private string? InstallAsset(string filePath, string repoName, bool silentInstall)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();

        if (ext == ".msi")
        {
            RunInstallerProcess(
                "msiexec.exe",
                silentInstall
                    ? $"/i \"{filePath}\" /qn /norestart"
                    : $"/i \"{filePath}\""
            );

            return null;
        }

        if (ext == ".exe")
        {
            string args = silentInstall
                ? DetectSilentArguments(filePath)
                : "";

            RunInstallerProcess(filePath, args);
            return null;
        }

        if (ext == ".zip")
        {
            string target = Path.Combine(InstallRoot, repoName);

            if (Directory.Exists(target))
                Directory.Delete(target, true);

            Directory.CreateDirectory(target);
            ZipFile.ExtractToDirectory(filePath, target, true);

            return target;
        }

        throw new Exception($"Unsupported file type: {ext}");
    }

    private string DetectSilentArguments(string filePath)
    {
        string name = Path.GetFileName(filePath).ToLowerInvariant();

        if (name.Contains("inno"))
        {
            Log("Detected: Inno Setup");
            return "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART";
        }

        if (name.Contains("nsis"))
        {
            Log("Detected: NSIS");
            return "/S";
        }

        if (name.Contains("squirrel"))
        {
            Log("Detected: Squirrel");
            return "--silent";
        }

        if (name.Contains("electron"))
        {
            Log("Detected: Electron Builder");
            return "--silent";
        }

        Log("Unknown installer type. Launching normal installer.");
        return "";
    }

    private void RunInstallerProcess(string fileName, string arguments)
    {
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = true
        });

        process?.WaitForExit();
    }

    private void CreateDesktopShortcut(string installPath, string repoName)
    {
        var exe = Directory.GetFiles(installPath, "*.exe", SearchOption.AllDirectories)
            .Where(x =>
            {
                string n = Path.GetFileName(x).ToLowerInvariant();

                return !n.Contains("uninstall") &&
                       !n.Contains("update") &&
                       !n.Contains("crash") &&
                       !n.Contains("helper");
            })
            .OrderByDescending(x =>
            {
                var info = FileVersionInfo.GetVersionInfo(x);
                int score = 0;

                if (!string.IsNullOrWhiteSpace(info.ProductName)) score += 100;
                if (!string.IsNullOrWhiteSpace(info.FileDescription)) score += 50;
                score += (int)Math.Min(new FileInfo(x).Length / 1024 / 1024, 50);

                return score;
            })
            .FirstOrDefault();

        if (exe == null)
        {
            Log("No executable detected for shortcut.");
            return;
        }

        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string shortcutPath = Path.Combine(desktop, repoName + ".lnk");

        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        dynamic shell = Activator.CreateInstance(shellType!)!;
        dynamic shortcut = shell.CreateShortcut(shortcutPath);

        shortcut.TargetPath = exe;
        shortcut.WorkingDirectory = Path.GetDirectoryName(exe);
        shortcut.IconLocation = exe;
        shortcut.Save();

        Log("Desktop shortcut created: " + shortcutPath);
    }

    private string SafeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');

        return name;
    }

    private void SetStep(string text, int percent, bool writeLog = true)
    {
        StepText.Text = text;
        ProgressBar.Value = percent;
        PercentText.Text = percent + "%";

        if (writeLog)
            Log(text);
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