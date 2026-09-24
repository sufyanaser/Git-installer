using System.Diagnostics;
using System.IO;
using GitHubAutoInstaller.Models;

namespace GitHubAutoInstaller.Services;

/// <summary>
/// Verifies the presence and version of local development and package management tools
/// without altering PATH, executing arbitrary scripts, or installing runtimes automatically.
/// </summary>
public class ToolVerificationService
{
    private readonly Func<string, string?> _pathLookup;
    private readonly Func<string, IReadOnlyList<string>, Task<(int ExitCode, string Output)>> _processRunner;

    public ToolVerificationService(
        Func<string, string?>? pathLookup = null,
        Func<string, IReadOnlyList<string>, Task<(int ExitCode, string Output)>>? processRunner = null)
    {
        _pathLookup = pathLookup ?? FindExecutableInPath;
        _processRunner = processRunner ?? RunToolVersionCheckAsync;
    }

    public virtual async Task<ToolRequirement> CheckToolAsync(
        string toolName,
        string? minimumVersion = null,
        CancellationToken cancellationToken = default)
    {
        string normalizedName = toolName.ToLowerInvariant().Trim();
        string executableName = normalizedName switch
        {
            "winget" => "winget.exe",
            "dotnet" => "dotnet.exe",
            "python" => "python.exe",
            "pip" => "pip.exe",
            "node" => "node.exe",
            "npm" => "npm.cmd",
            "cargo" => "cargo.exe",
            "rustc" => "rustc.exe",
            "docker" => "docker.exe",
            "powershell" => "powershell.exe",
            _ => normalizedName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                 normalizedName.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                ? normalizedName
                : normalizedName + ".exe"
        };

        string? foundPath = _pathLookup(executableName);
        if (foundPath is null)
        {
            return new ToolRequirement(
                ToolName: toolName,
                IsInstalled: false,
                FoundVersion: null,
                MinimumVersion: minimumVersion,
                ResolutionGuidance: GetInstallationGuidance(normalizedName));
        }

        string? version = null;
        try
        {
            IReadOnlyList<string> versionArgs = normalizedName is "pip" or "npm" or "docker" or "cargo" or "dotnet"
                ? ["--version"]
                : ["--version"];

            (int exitCode, string output) = await _processRunner(foundPath, versionArgs);
            if (exitCode == 0 && !string.IsNullOrWhiteSpace(output))
            {
                version = ParseVersionString(output);
            }
        }
        catch
        {
            // If running --version fails, we still know the executable was found in PATH.
            version = "Installed (version unknown)";
        }

        return new ToolRequirement(
            ToolName: toolName,
            IsInstalled: true,
            FoundVersion: version ?? "Installed",
            MinimumVersion: minimumVersion,
            ResolutionGuidance: "Tool is installed and accessible in PATH.");
    }

    public static string? FindExecutableInPath(string executableName)
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv))
        {
            return null;
        }

        string[] directories = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string directory in directories)
        {
            try
            {
                string candidate = Path.Combine(directory, executableName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                // If executableName does not have an extension, also check with .exe and .cmd
                if (!Path.HasExtension(executableName))
                {
                    if (File.Exists(candidate + ".exe")) return candidate + ".exe";
                    if (File.Exists(candidate + ".cmd")) return candidate + ".cmd";
                }
            }
            catch
            {
                // Ignore invalid PATH entries
            }
        }

        // Check common default locations on Windows
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        string[] commonLocations = [
            Path.Combine(localAppData, @"Microsoft\WindowsApps", executableName),
            Path.Combine(localAppData, @"Programs\Python", executableName),
            Path.Combine(programFiles, @"dotnet", executableName),
            Path.Combine(programFiles, @"nodejs", executableName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @".cargo\bin", executableName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @".dotnet", executableName)
        ];

        foreach (string candidate in commonLocations)
        {
            try
            {
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }

        return null;
    }

    private static async Task<(int ExitCode, string Output)> RunToolVersionCheckAsync(
        string executablePath,
        IReadOnlyList<string> arguments)
    {
        using Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        foreach (string arg in arguments)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        process.Start();

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        string stdout = await process.StandardOutput.ReadToEndAsync(cts.Token);
        await process.WaitForExitAsync(cts.Token);

        return (process.ExitCode, stdout.Trim());
    }

    private static string ParseVersionString(string raw)
    {
        string firstLine = raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? raw;
        string[] tokens = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (string token in tokens)
        {
            string clean = token.TrimStart('v', 'V');
            if (clean.Length > 0 && char.IsDigit(clean[0]) && clean.Contains('.'))
            {
                return clean;
            }
        }

        return firstLine.Length > 40 ? firstLine[..40] + "..." : firstLine;
    }

    private static string GetInstallationGuidance(string tool) => tool switch
    {
        "winget" => "WinGet (Windows Package Manager) is included in Windows 10/11 via App Installer from the Microsoft Store.",
        "dotnet" => "Download the .NET SDK from https://dotnet.microsoft.com/download.",
        "python" => "Install Python for Windows from https://www.python.org or via WinGet: 'winget install Python.Python.3.12'.",
        "pip" => "Ensure Python is installed and 'pip' is enabled in your Python installation.",
        "node" => "Install Node.js LTS from https://nodejs.org or via WinGet: 'winget install OpenJS.NodeJS.LTS'.",
        "npm" => "npm is bundled with Node.js. Install Node.js from https://nodejs.org.",
        "cargo" or "rustc" => "Install Rust via rustup from https://rustup.rs or 'winget install Rustlang.Rustup'.",
        "docker" => "Install Docker Desktop from https://www.docker.com/products/docker-desktop.",
        "powershell" => "PowerShell is built into Windows, or install PowerShell 7 via 'winget install Microsoft.PowerShell'.",
        _ => $"Install '{tool}' manually or configure it in your system PATH."
    };
}
