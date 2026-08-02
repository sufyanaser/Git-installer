using System.IO;
using System.Net.Http;
using GitHubAutoInstaller.Models;

namespace GitHubAutoInstaller.Services;

public sealed class FileDownloadService
{
    private readonly HttpClient _httpClient;

    public FileDownloadService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<string> DownloadAsync(
        ReleaseAsset asset,
        string directory,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
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

                    if (contentLength is > 0)
                    {
                        progress.Report(Math.Clamp((double)bytesRead / contentLength.Value, 0, 1));
                    }
                }

                await output.FlushAsync(cancellationToken);
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
