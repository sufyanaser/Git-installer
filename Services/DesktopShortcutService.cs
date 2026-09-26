using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace GitHubAutoInstaller.Services;

public static class DesktopShortcutService
{
    public static string? CreateForBestExecutable(string installDirectory, string repositoryName)
    {
        EnumerationOptions options = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true
        };

        string? executable = Directory
            .EnumerateFiles(installDirectory, "*.exe", options)
            .Where(path => !ContainsExcludedExecutableName(path))
            .OrderByDescending(ScoreExecutable)
            .FirstOrDefault();

        if (executable is null)
        {
            return null;
        }

        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(desktop))
        {
            return null;
        }

        Directory.CreateDirectory(desktop);

        string sanitizedRepo = string.Concat(repositoryName.Split(Path.GetInvalidFileNameChars()));
        if (string.IsNullOrWhiteSpace(sanitizedRepo))
        {
            sanitizedRepo = "Application";
        }

        string shortcutPath = Path.Combine(desktop, sanitizedRepo + ".lnk");

        Type shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Windows Script Host is unavailable.");
        object shell = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("Windows Script Host could not be started.");
        object? shortcut = null;

        try
        {
            dynamic dynamicShell = shell;
            shortcut = dynamicShell.CreateShortcut(shortcutPath);
            dynamic dynamicShortcut = shortcut;
            dynamicShortcut.TargetPath = executable;
            dynamicShortcut.WorkingDirectory = Path.GetDirectoryName(executable);
            dynamicShortcut.IconLocation = executable;
            dynamicShortcut.Save();
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut))
            {
                Marshal.FinalReleaseComObject(shortcut);
            }

            if (Marshal.IsComObject(shell))
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }

        return shortcutPath;
    }

    private static bool ContainsExcludedExecutableName(string path)
    {
        string name = Path.GetFileName(path).ToLowerInvariant();
        return name.Contains("uninstall", StringComparison.Ordinal) ||
               name.Contains("update", StringComparison.Ordinal) ||
               name.Contains("crash", StringComparison.Ordinal) ||
               name.Contains("helper", StringComparison.Ordinal);
    }

    private static int ScoreExecutable(string path)
    {
        try
        {
            FileVersionInfo version = FileVersionInfo.GetVersionInfo(path);
            int score = 0;

            if (!string.IsNullOrWhiteSpace(version.ProductName)) score += 100;
            if (!string.IsNullOrWhiteSpace(version.FileDescription)) score += 50;
            score += (int)Math.Min(new FileInfo(path).Length / 1_048_576, 50);

            return score;
        }
        catch
        {
            return 0;
        }
    }
}
