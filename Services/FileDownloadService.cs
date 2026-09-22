using System.IO;
using System.Net.Http;
using GitHubAutoInstaller.Models;

namespace GitHubAutoInstaller.Services;

public sealed class FileDownloadService
{
    public const long DefaultMaximumDownloadBytes = 2L * 1024 * 1024 * 1024;

    private readonly HttpClient _httpClient;
    private readonly long _maximumDownloadBytes;

    public FileDownloadService(
        HttpClient httpClient,
        long maximumDownloadBytes = DefaultMaximumDownloadBytes)
    {
        _httpClient = httpClient;
        _maximumDownloadBytes = maximumDownloadBytes > 0
            ? maximumDownloadBytes
            : throw new ArgumentOutOfRangeException(nameof(maximumDownloadBytes));
    }

    public async Task<string> DownloadAsync(
        ReleaseAsset asset,
        string directory,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        if (asset.DownloadUrl.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Release assets must use a secure HTTPS download URL.");
        }

        if (asset.Size > _maximumDownloadBytes)
        {
            throw new InvalidOperationException(
                $"The release asset exceeds the {FormatBytes(_maximumDownloadBytes)} download limit.");
        }

        Directory.CreateDirectory(directory);

        string outputPath = Path.Combine(directory, ToSafeFileName(asset.Name));
        string temporaryPath = outputPath + ".part";

        if (File.Exists(temporaryPath))
        {
            File.Delete(temporaryPath);
        }

        try
        {
            using HttpResponseMessage response = await _httpClient.GetAsync(
                asset.DownloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            long? contentLength = response.Content.Headers.ContentLength;
            if (contentLength > _maximumDownloadBytes)
            {
                throw new InvalidOperationException(
                    $"The server response exceeds the {FormatBytes(_maximumDownloadBytes)} download limit.");
            }
            await using (Stream input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (FileStream output = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81_920,
                useAsync: true))
            {
                byte[] buffer = new byte[81_920];
                long bytesRead = 0;

                while (true)
                {
                    int read = await input.ReadAsync(buffer, cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    bytesRead += read;

                    if (bytesRead > _maximumDownloadBytes)
                    {
                        throw new InvalidOperationException(
                            $"The download exceeded the {FormatBytes(_maximumDownloadBytes)} limit.");
                    }

                    if (contentLength is > 0)
                    {
                        progress.Report(Math.Clamp((double)bytesRead / contentLength.Value, 0, 1));
                    }
                }

                await output.FlushAsync(cancellationToken);

                if (asset.Size > 0 && bytesRead != asset.Size)
                {
                    throw new InvalidOperationException(
                        $"The downloaded size ({bytesRead} bytes) does not match GitHub's asset size ({asset.Size} bytes).");
                }
            }

            File.Move(temporaryPath, outputPath, overwrite: true);
            progress.Report(1);

            return outputPath;
        }
        catch
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            throw;
        }
    }

    private static string FormatBytes(long bytes) => $"{bytes / (1024d * 1024 * 1024):0.##} GB";

    private static string ToSafeFileName(string fileName)
    {
        string sanitized = fileName;
        foreach (char invalidCharacter in Path.GetInvalidFileNameChars())
        {
            sanitized = sanitized.Replace(invalidCharacter, '_');
        }

        return string.IsNullOrWhiteSpace(sanitized)
            ? throw new InvalidOperationException("GitHub returned an invalid asset file name.")
            : sanitized;
    }
}
