using GitHubAutoInstaller.Models;

namespace GitHubAutoInstaller.Services;

public static class GitHubRepositoryParser
{
    public static GitHubRepository Parse(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri? uri) ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Enter a valid HTTPS GitHub repository URL.", nameof(value));
        }

        string[] segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments.Length < 2)
        {
            throw new ArgumentException(
                "Repository URL must be in the form https://github.com/owner/repository.",
                nameof(value));
        }

        string owner = segments[0];
        string repository = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? segments[1][..^4]
            : segments[1];

        if (segments.Length > 2 && !IsSupportedSubpath(segments[2]))
        {
            throw new ArgumentException(
                "Repository URL must be in the form https://github.com/owner/repository.",
                nameof(value));
        }

        if (!IsValidOwner(owner) || !IsValidRepository(repository))
        {
            throw new ArgumentException("The GitHub owner or repository name is invalid.", nameof(value));
        }

        return new GitHubRepository(owner, repository);
    }

    private static bool IsSupportedSubpath(string segment) =>
        segment.Equals("releases", StringComparison.OrdinalIgnoreCase) ||
        segment.Equals("tree", StringComparison.OrdinalIgnoreCase) ||
        segment.Equals("blob", StringComparison.OrdinalIgnoreCase) ||
        segment.Equals("tags", StringComparison.OrdinalIgnoreCase) ||
        segment.Equals("commits", StringComparison.OrdinalIgnoreCase) ||
        segment.Equals("commit", StringComparison.OrdinalIgnoreCase) ||
        segment.Equals("actions", StringComparison.OrdinalIgnoreCase) ||
        segment.Equals("issues", StringComparison.OrdinalIgnoreCase) ||
        segment.Equals("pulls", StringComparison.OrdinalIgnoreCase);

    private static bool IsValidOwner(string value) =>
        value.Length is > 0 and <= 39 &&
        value[0] != '-' &&
        value[^1] != '-' &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');

    private static bool IsValidRepository(string value) =>
        value.Length is > 0 and <= 100 &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
}
