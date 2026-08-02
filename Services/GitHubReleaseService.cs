using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using GitHubAutoInstaller.Models;

namespace GitHubAutoInstaller.Services;

public sealed class GitHubReleaseService
{
    private readonly HttpClient _httpClient;

    public GitHubReleaseService(HttpClient httpClient)
    {
        _httpClient = httpClient;

        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("GitHubAutoInstaller", "1.0"));
        }

        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public async Task<RepositoryMetadata> GetRepositoryAsync(
        GitHubRepository repository,
        CancellationToken cancellationToken)
    {
        using JsonDocument json = await GetJsonAsync(
            $"https://api.github.com/repos/{repository.Owner}/{repository.Name}",
            cancellationToken);

        JsonElement root = json.RootElement;
        string fullName = GetRequiredString(root, "full_name");
        string description = root.TryGetProperty("description", out JsonElement descriptionElement)
            ? descriptionElement.GetString() ?? "No description provided."
            : "No description provided.";
        int stars = root.TryGetProperty("stargazers_count", out JsonElement starsElement)
            ? starsElement.GetInt32()
            : 0;

        Uri? avatarUrl = null;
        if (root.TryGetProperty("owner", out JsonElement owner) &&
            owner.TryGetProperty("avatar_url", out JsonElement avatar) &&
            Uri.TryCreate(avatar.GetString(), UriKind.Absolute, out Uri? parsedAvatar))
        {
            avatarUrl = parsedAvatar;
        }

        return new RepositoryMetadata(fullName, description, stars, avatarUrl);
    }

    public async Task<GitHubRelease> GetLatestReleaseAsync(
        GitHubRepository repository,
        CancellationToken cancellationToken)
    {
        using JsonDocument json = await GetJsonAsync(
            $"https://api.github.com/repos/{repository.Owner}/{repository.Name}/releases/latest",
            cancellationToken);

        JsonElement root = json.RootElement;
        string tagName = GetRequiredString(root, "tag_name");
        Uri pageUrl = new(GetRequiredString(root, "html_url"));

        if (!root.TryGetProperty("assets", out JsonElement assetsElement) ||
            assetsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("The latest release does not contain downloadable assets.");
        }

        List<ReleaseAsset> assets = [];
        foreach (JsonElement asset in assetsElement.EnumerateArray())
        {
            assets.Add(new ReleaseAsset(
                GetRequiredString(asset, "name"),
                new Uri(GetRequiredString(asset, "browser_download_url")),
                asset.TryGetProperty("size", out JsonElement size) ? size.GetInt64() : 0,
                asset.TryGetProperty("content_type", out JsonElement contentType)
                    ? contentType.GetString() ?? "application/octet-stream"
                    : "application/octet-stream"));
        }

        return new GitHubRelease(tagName, pageUrl, assets);
    }

    private async Task<JsonDocument> GetJsonAsync(string requestUri, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _httpClient.GetAsync(requestUri, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(
                "The repository or latest release was not found. Verify that it is public and has a published release.");
        }

        if (response.StatusCode == HttpStatusCode.Forbidden &&
            response.Headers.TryGetValues("X-RateLimit-Remaining", out IEnumerable<string>? remaining) &&
            remaining.FirstOrDefault() == "0")
        {
            throw new InvalidOperationException(
                "GitHub API rate limit reached. Wait for the limit to reset, then try again.");
        }

        response.EnsureSuccessStatusCode();
        Stream content = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken);
    }

    private static string GetRequiredString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property) ||
            string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new InvalidOperationException($"GitHub returned an invalid '{propertyName}' value.");
        }

        return property.GetString()!;
    }
}
