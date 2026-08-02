using System.IO.Compression;
using System.Net;
using System.Net.Http;
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

string testRoot = Path.Combine(
    Path.GetTempPath(),
    "GitHubAutoInstallerSmokeTests",
    Guid.NewGuid().ToString("N"));

try
{
    Directory.CreateDirectory(testRoot);
    await VerifyAtomicDownloadAsync(testRoot);
    await VerifyZipInstallationAsync(testRoot);
}
finally
{
    if (Directory.Exists(testRoot))
    {
        Directory.Delete(testRoot, recursive: true);
    }
}

Console.WriteLine("Smoke tests passed.");
return;

static async Task VerifyAtomicDownloadAsync(string testRoot)
{
    byte[] payload = "verified-download"u8.ToArray();
    using HttpClient httpClient = new(new StaticResponseHandler(payload));
    FileDownloadService service = new(httpClient);
    string downloadDirectory = Path.Combine(testRoot, "downloads");

    string path = await service.DownloadAsync(
        Asset("verified-windows-x64.zip", payload.Length),
        downloadDirectory,
        new Progress<double>(),
        CancellationToken.None);

    Assert(File.ReadAllBytes(path).SequenceEqual(payload), "Atomic download payload failed.");
    Assert(!File.Exists(path + ".part"), "Atomic download left a partial file.");
}

static async Task VerifyZipInstallationAsync(string testRoot)
{
    string zipPath = Path.Combine(testRoot, "portable.zip");
    using (ZipArchive archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
    {
        ZipArchiveEntry executable = archive.CreateEntry("app/application.exe");
        await using Stream stream = executable.Open();
        await stream.WriteAsync("executable"u8.ToArray());
    }

    AssetInstallerService service = new(Path.Combine(testRoot, "installed"));
    InstallationResult result = await service.InstallAsync(
        zipPath,
        "verified-repository",
        silent: false,
        _ => { },
        CancellationToken.None);

    Assert(result.ExitCode == 0, "ZIP install returned an invalid exit code.");
    Assert(
        File.Exists(Path.Combine(result.InstalledDirectory!, "app", "application.exe")),
        "ZIP install did not extract the expected executable.");
}

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

file sealed class StaticResponseHandler(byte[] payload) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response = new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload)
        };
        response.Content.Headers.ContentLength = payload.Length;
        return Task.FromResult(response);
    }
}
