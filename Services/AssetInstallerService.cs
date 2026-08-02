using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using GitHubAutoInstaller.Models;

namespace GitHubAutoInstaller.Services;

public sealed class AssetInstallerService
{
    private readonly string _installRoot;

    public AssetInstallerService(string installRoot)
    {
        _installRoot = installRoot;
        Directory.CreateDirectory(_installRoot);
    }

    public async Task<InstallationResult> InstallAsync(
        string filePath,
        string repositoryName,
        bool silent,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        string extension = Path.GetExtension(filePath).ToLowerInvariant();

        return extension switch
        {
            ".msi" => new InstallationResult(
                null,
                await RunProcessAsync(
                    "msiexec.exe",
                    silent
                        ? $"/i \"{filePath}\" /qn /norestart"
                        : $"/i \"{filePath}\"",
                    cancellationToken)),
            ".exe" => new InstallationResult(
                null,
                await RunProcessAsync(
                    filePath,
                    silent ? DetectSilentArguments(filePath, log) : string.Empty,
                    cancellationToken)),
            ".zip" => new InstallationResult(
                await Task.Run(
                    () => ExtractZipAtomically(filePath, repositoryName, cancellationToken),
                    cancellationToken),
                0),
            _ => throw new InvalidOperationException($"Unsupported asset type: {extension}")
        };
    }

    private static string DetectSilentArguments(string filePath, Action<string> log)
    {
        string name = Path.GetFileName(filePath).ToLowerInvariant();

        if (name.Contains("inno", StringComparison.Ordinal))
        {
            log("Detected Inno Setup silent mode.");
            return "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART";
        }

        if (name.Contains("nsis", StringComparison.Ordinal))
        {
            log("Detected NSIS silent mode.");
            return "/S";
        }

        if (name.Contains("squirrel", StringComparison.Ordinal) ||
            name.Contains("electron", StringComparison.Ordinal))
        {
            log("Detected Electron/Squirrel silent mode.");
            return "--silent";
        }

        log("Installer type is unknown; launching interactively to avoid unsafe silent flags.");
        return string.Empty;
    }

    private static async Task<int> RunProcessAsync(
        string fileName,
        string arguments,
        CancellationToken cancellationToken)
    {
        using Process process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = true
        }) ?? throw new InvalidOperationException("Windows could not start the installer process.");

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }

        if (process.ExitCode is not (0 or 1_641 or 3_010))
        {
            throw new InvalidOperationException(
                $"Installer exited with code {process.ExitCode}.");
        }

        return process.ExitCode;
    }

    private string ExtractZipAtomically(
        string filePath,
        string repositoryName,
        CancellationToken cancellationToken)
    {
        string safeRepositoryName = new(repositoryName
            .Where(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')
            .ToArray());

        if (string.IsNullOrWhiteSpace(safeRepositoryName))
        {
            throw new InvalidOperationException("Repository name cannot be used as an install directory.");
        }

        string target = Path.Combine(_installRoot, safeRepositoryName);
        string temporary = target + ".new-" + Guid.NewGuid().ToString("N");
        string backup = target + ".backup-" + Guid.NewGuid().ToString("N");

        Directory.CreateDirectory(temporary);

        try
        {
            using ZipArchive archive = ZipFile.OpenRead(filePath);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string destination = Path.GetFullPath(Path.Combine(temporary, entry.FullName));
                string extractionRoot = Path.GetFullPath(temporary) + Path.DirectorySeparatorChar;
                if (!destination.StartsWith(extractionRoot, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("The ZIP asset contains an unsafe file path.");
                }

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);
            }

            if (Directory.Exists(target))
            {
                Directory.Move(target, backup);
            }

            Directory.Move(temporary, target);

            if (Directory.Exists(backup))
            {
                try
                {
                    Directory.Delete(backup, recursive: true);
                }
                catch (IOException)
                {
                    // The installed target is valid. A locked backup can be removed on a later run.
                }
            }

            return target;
        }
        catch
        {
            if (Directory.Exists(temporary))
            {
                Directory.Delete(temporary, recursive: true);
            }

            if (!Directory.Exists(target) && Directory.Exists(backup))
            {
                Directory.Move(backup, target);
            }

            throw;
        }
    }
}
