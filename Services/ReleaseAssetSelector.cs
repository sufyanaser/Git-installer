using System.IO;
using GitHubAutoInstaller.Models;

namespace GitHubAutoInstaller.Services;

public static class ReleaseAssetSelector
{
    public static ReleaseAsset SelectBestWindowsX64Asset(IEnumerable<ReleaseAsset> assets)
    {
        ReleaseAsset? selected = assets
            .Select(asset => new { Asset = asset, Score = Score(asset.Name) })
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Asset.Size)
            .Select(candidate => candidate.Asset)
            .FirstOrDefault();

        return selected ?? throw new InvalidOperationException(
            "No supported Windows asset was found. Supported formats: .exe, .msi, .zip, and .ps1.");
    }

    internal static int Score(string fileName)
    {
        string name = fileName
            .ToLowerInvariant()
            .Replace("x86_64", "x64", StringComparison.Ordinal)
            .Replace("x86-64", "x64", StringComparison.Ordinal);
        string extension = Path.GetExtension(name);

        int score = extension switch
        {
            ".exe" => 300,
            ".msi" => 280,
            ".zip" => 180,
            ".ps1" => 120,
            _ => -1_000
        };

        if (ContainsAny(name, "arm64", "aarch64", "armv7", "win32", "ia32", "x86"))
        {
            return -1_000;
        }

        if (ContainsAny(name, "linux", "darwin", "macos", "osx", "appimage"))
        {
            return -1_000;
        }

        if (ContainsAny(name, "checksum", "checksums", "sha256", "symbols", "debug", "blockmap"))
        {
            return -1_000;
        }

        if (ContainsAny(name, "x64", "amd64", "win64")) score += 140;
        if (ContainsAny(name, "windows", "win")) score += 80;
        if (ContainsAny(name, "setup", "installer", "install")) score += 40;
        if (ContainsAny(name, "portable")) score -= 30;

        return score;
    }

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(value.Contains);
}
