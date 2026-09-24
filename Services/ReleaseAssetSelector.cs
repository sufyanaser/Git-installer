using System.IO;
using GitHubAutoInstaller.Models;

namespace GitHubAutoInstaller.Services;

public static class ReleaseAssetSelector
{
    public static readonly string[] SupportedExtensions = [".exe", ".msi", ".zip", ".7z", ".ps1"];

    public static ReleaseAsset SelectBestWindowsX64Asset(IEnumerable<ReleaseAsset> assets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        IReadOnlyList<ReleaseAsset> availableAssets = assets.ToList();

        ReleaseAsset? selected = availableAssets
            .Select(asset => new { Asset = asset, Score = Score(asset.Name) })
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Asset.Size)
            .Select(candidate => candidate.Asset)
            .FirstOrDefault();

        if (selected is not null)
        {
            return selected;
        }

        string detail = availableAssets.Count == 0
            ? "The release has no uploaded assets. GitHub's automatic source archives are not Windows installers."
            : $"The release assets are not compatible: {string.Join(", ", availableAssets.Select(asset => asset.Name))}.";

        throw new InvalidOperationException(
            $"No supported Windows x64 asset was found. {detail} Supported formats: {string.Join(", ", SupportedExtensions)}.");
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
            ".7z" => 175,
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
