using GitHubAutoInstaller.Models;
using GitHubAutoInstaller.Services;

GitHubRepository repository = GitHubRepositoryParser.Parse(
    "https://github.com/sufyanaser/Git-installer.git");

Assert(repository.Owner == "sufyanaser", "Repository owner parsing failed.");
Assert(repository.Name == "Git-installer", "Repository name parsing failed.");

AssertThrows<ArgumentException>(
    () => GitHubRepositoryParser.Parse("https://example.com/owner/repository"),
    "Non-GitHub hosts must be rejected.");

ReleaseAsset selected = ReleaseAssetSelector.SelectBestWindowsX64Asset(
[
    Asset("application-linux-x64.zip", 800),
    Asset("application-windows-arm64.exe", 900),
    Asset("application-windows-x64.zip", 700),
    Asset("application-windows-x64-setup.exe", 600)
]);

Assert(
    selected.Name == "application-windows-x64-setup.exe",
    "Windows x64 installer selection failed.");

Console.WriteLine("Smoke tests passed.");
return;

static ReleaseAsset Asset(string name, long size) => new(
    name,
    new Uri($"https://example.com/{name}"),
    size,
    "application/octet-stream");

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertThrows<TException>(Action action, string message)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException(message);
}
