using System.Diagnostics;
using System.IO;

namespace GitHubAutoInstaller.Services;

/// <summary>
/// Executes external processes using structured arguments to avoid command injection,
/// with real-time output logging and graceful process-tree termination on cancellation.
/// </summary>
public static class ProcessExecutionService
{
    public static async Task<int> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        Action<string> log,
        CancellationToken cancellationToken,
        bool requireZeroExitCode = true,
        IEnumerable<int>? acceptableExitCodes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        arguments ??= [];

        HashSet<int> allowedExitCodes = acceptableExitCodes is not null
            ? new HashSet<int>(acceptableExitCodes)
            : [0];

        ProcessStartInfo startInfo = new()
        {
            FileName = executable,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? Environment.CurrentDirectory : workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = new() { StartInfo = startInfo };

        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                log(e.Data);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                log("[STDERR] " + e.Data);
            }
        };

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Windows could not start process '{executable}'.");
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"Failed to launch '{executable}': {ex.Message}", ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Ignore errors during termination
                }
            }

            throw;
        }

        if (requireZeroExitCode && !allowedExitCodes.Contains(process.ExitCode))
        {
            throw new InvalidOperationException(
                $"Command '{Path.GetFileName(executable)}' exited with error code {process.ExitCode}.");
        }

        return process.ExitCode;
    }
}
