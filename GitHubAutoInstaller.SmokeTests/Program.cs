using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using GitHubAutoInstaller.Models;
using GitHubAutoInstaller.Services;
using GitHubAutoInstaller.Services.Adapters;
using SharpCompress.Writers;
using SharpCompress.Writers.SevenZip;

Console.WriteLine("Running GitHub Auto Installer Smoke Tests...");

// ============================================================================
// 1. Repository URL Parsing and Validation
// ============================================================================
Console.WriteLine("[1/11] Testing repository parsing and validation...");
GitHubRepository repository = GitHubRepositoryParser.Parse("https://github.com/sufyanaser/Git-installer.git");
Assert(repository.Owner == "sufyanaser", "Repository owner parsing failed.");
Assert(repository.Name == "Git-installer", "Repository name parsing failed.");
Assert(repository.FullName == "sufyanaser/Git-installer", "Repository full name failed.");

AssertThrows<ArgumentException>(
    () => GitHubRepositoryParser.Parse("https://example.com/owner/repository"),
    "Non-GitHub hosts must be rejected.");

AssertThrows<ArgumentException>(
    () => GitHubRepositoryParser.Parse("http://github.com/owner/repository"),
    "Insecure HTTP URLs must be rejected.");

AssertThrows<ArgumentException>(
    () => GitHubRepositoryParser.Parse("https://github.com/invalid-owner-/repo"),
    "Invalid owner format must be rejected.");

// ============================================================================
// 2. Windows x64 Asset Scoring and Architecture Filtering
// ============================================================================
Console.WriteLine("[2/11] Testing Windows x64 asset scoring and architecture rejection...");
ReleaseAsset selected = ReleaseAssetSelector.SelectBestWindowsX64Asset(
[
    Asset("application-linux-x64.zip", 800),
    Asset("application-windows-arm64.exe", 900),
    Asset("application-windows-x64.zip", 700),
    Asset("application-windows-x64-setup.exe", 600)
]);
Assert(selected.Name == "application-windows-x64-setup.exe", "Windows x64 installer selection failed.");

ReleaseAsset sevenZipSelected = ReleaseAssetSelector.SelectBestWindowsX64Asset(
[
    Asset("application-linux-x64.tar.gz", 800),
    Asset("application-windows-x64.7z", 750)
]);
Assert(sevenZipSelected.Name == "application-windows-x64.7z", "Windows x64 .7z asset selection failed.");

ReleaseAsset x86AliasSelected = ReleaseAssetSelector.SelectBestWindowsX64Asset(
[
    Asset("application-windows-x86-setup.exe", 900),
    Asset("application-windows-x86_64.zip", 800)
]);
Assert(x86AliasSelected.Name == "application-windows-x86_64.zip", "Valid x86_64 alias was rejected as 32-bit.");

ReleaseAsset powerShellSelected = ReleaseAssetSelector.SelectBestWindowsX64Asset(
[
    Asset("checksums.txt", 900),
    Asset("Get.ps1", 800)
]);
Assert(powerShellSelected.Name == "Get.ps1", "PowerShell-only release asset was not selected.");

AssertThrows<InvalidOperationException>(
    () => ReleaseAssetSelector.SelectBestWindowsX64Asset([]),
    "Release without uploaded assets must be rejected.");

AssertThrows<InvalidOperationException>(
    () => ReleaseAssetSelector.SelectBestWindowsX64Asset([Asset("package.whl", 100)]),
    "Package-manager artifact must not be treated as a Windows installer.");

AssertThrows<InvalidOperationException>(
    () => ReleaseAssetSelector.SelectBestWindowsX64Asset([
        Asset("app-linux-x64.tar.gz", 500),
        Asset("app-macos-arm64.zip", 500)
    ]),
    "Incompatible architectures (Linux/macOS) must be rejected.");

// ============================================================================
// 3. Repository Manifest Detection & Classification
// ============================================================================
Console.WriteLine("[3/11] Testing repository manifest detection and classification...");
IReadOnlyList<RepositoryInstallOption> detectedLegacy = InstallationPlanService.DetectFromRootFiles(
    ["package.json", "pyproject.toml", "Cargo.toml", "app.csproj", "README.md"]);
Assert(detectedLegacy.Count == 4, "Legacy manifest detection count mismatch.");
Assert(InstallationPlanService.DetectFromRootFiles(["docs/package.json"]).Count == 0,
    "Nested manifests must not be mistaken for root manifests.");

// WinGet manifest detection
IReadOnlyList<RepositoryInstallOption> wingetDetected = InstallationPlanService.DetectFromRootFiles(["winget.yaml"]);
Assert(wingetDetected.Any(o => o.Tool == "winget"), "WinGet manifest detection failed.");

// Classification with releases
RepositoryClassification releaseClass = RepositoryInspectorService.Classify(
    new GitHubRepository("owner", "tool"),
    new GitHubRelease("v1.0.0", new Uri("https://github.com/owner/tool/releases/v1.0.0"), [Asset("tool-x64.exe", 1000)]),
    ["README.md"]);
Assert(releaseClass.Category == RepositoryCategory.DirectlyInstallableApplication, "EXE release category mismatch.");
Assert(releaseClass.IsWindowsCompatible, "Windows release must be compatible.");

// Classification with portable 7z
RepositoryClassification sevenZipClass = RepositoryInspectorService.Classify(
    new GitHubRepository("owner", "tool"),
    new GitHubRelease("v1.0.0", new Uri("https://github.com/owner/tool/releases/v1.0.0"), [Asset("tool-win-x64.7z", 1000)]),
    ["README.md"]);
Assert(sevenZipClass.Category == RepositoryCategory.PortableApplication, "7z release category mismatch.");

// Classification with Docker only
RepositoryClassification dockerClass = RepositoryInspectorService.Classify(
    new GitHubRepository("owner", "server"),
    null,
    ["Dockerfile", "compose.yaml"]);
Assert(dockerClass.DetectedEcosystems.Contains(RepositoryEcosystem.Docker), "Docker ecosystem detection failed.");
Assert(!dockerClass.CanInstallAutomatically, "Docker container launching must be guarded.");

// ============================================================================
// 4. Conflicting Installation Methods & Prioritization
// ============================================================================
Console.WriteLine("[4/11] Testing conflicting installation methods and priority ordering...");
ToolVerificationService mockAllToolsInstalled = new(
    pathLookup: _ => @"C:\Tools\mock.exe",
    processRunner: (_, _) => Task.FromResult((0, "1.0.0")));

IReadOnlyList<InstallationOption> conflictOptions = await InstallationPlanService.GenerateOptionsAsync(
    new GitHubRepository("owner", "hybrid-app"),
    "v2.0.0",
    new GitHubRelease("v2.0.0", new Uri("https://github.com/owner/hybrid-app/releases/v2.0.0"), [Asset("hybrid-setup.exe", 5000)]),
    ["package.json", "pyproject.toml", "winget.yaml"],
    mockAllToolsInstalled);

Assert(conflictOptions.Count >= 3, "Conflicting options should all be surfaced to the user.");
Assert(conflictOptions[0].IsRecommended, "First option must be marked as recommended.");
Assert(conflictOptions[0].Method == InstallationMethod.WindowsExecutableExe,
    "Official Windows release asset must be prioritized over package managers.");
Assert(conflictOptions.Any(o => o.Method == InstallationMethod.WinGetPackage),
    "WinGet option should be available when manifest is present.");
Assert(conflictOptions.Any(o => o.Method == InstallationMethod.NpmPackage),
    "npm option should be available when package.json is present.");

// ============================================================================
// 5. Unsupported Project Types & Actionable Guidance
// ============================================================================
Console.WriteLine("[5/11] Testing unsupported repository types and actionable explanations...");

// Explicit library repository
RepositoryClassification libClass = RepositoryInspectorService.Classify(
    new GitHubRepository("owner", "string-utils-lib"),
    null,
    ["Cargo.toml"],
    manifestSnippet: "[lib]\nname = \"string_utils\"");
Assert(libClass.Category == RepositoryCategory.PackageOrLibrary, "Library must be classified as PackageOrLibrary.");
Assert(!libClass.CanInstallAutomatically, "Library must not be installable automatically.");
Assert(!string.IsNullOrWhiteSpace(libClass.UnsupportedReason), "Library must provide an unsupported reason.");

// Ambiguous source-only repo
RepositoryClassification sourceOnlyClass = RepositoryInspectorService.Classify(
    new GitHubRepository("owner", "docs-repo"),
    null,
    ["index.html", "style.css", "README.md"]);
Assert(sourceOnlyClass.Category == RepositoryCategory.UnsupportedOrAmbiguous, "Ambiguous repo classification mismatch.");
Assert(!sourceOnlyClass.CanInstallAutomatically, "Ambiguous repo must not be auto-installed.");
Assert(!string.IsNullOrWhiteSpace(sourceOnlyClass.UnsupportedReason), "Reason must be present for unsupported repo.");

// Release with no Windows assets
RepositoryClassification linuxOnlyRelease = RepositoryInspectorService.Classify(
    new GitHubRepository("owner", "linux-tool"),
    new GitHubRelease("v1.0", new Uri("https://example.com"), [Asset("tool-linux-x64.tar.gz", 200)]),
    ["README.md"]);
Assert(linuxOnlyRelease.Category == RepositoryCategory.UnsupportedOrAmbiguous, "Linux-only release must be unsupported.");
Assert(!linuxOnlyRelease.IsWindowsCompatible, "Linux-only release is not Windows compatible.");

// ============================================================================
// 6. Missing Dependencies & Tool Verification
// ============================================================================
Console.WriteLine("[6/11] Testing missing dependencies and pre-flight validation...");
ToolVerificationService mockNoTools = new(
    pathLookup: _ => null,
    processRunner: (_, _) => Task.FromResult((1, string.Empty)));

ToolRequirement missingWinget = await mockNoTools.CheckToolAsync("winget");
Assert(!missingWinget.IsInstalled, "Mock missing tool must report IsInstalled == false.");
Assert(missingWinget.ResolutionGuidance.Contains("Windows Package Manager"), "Guidance must assist the user.");

IReadOnlyList<InstallationOption> optionsWithoutTools = await InstallationPlanService.GenerateOptionsAsync(
    new GitHubRepository("owner", "python-cli"),
    "v1.0.0",
    null,
    ["pyproject.toml"],
    mockNoTools);

Assert(optionsWithoutTools.Count > 0, "Option should be generated even if tool is missing.");
InstallationPlan blockedPlan = optionsWithoutTools[0].Plan;
Assert(!blockedPlan.CanExecuteAutomatically, "Plan must be marked blocked when required tools are missing.");
Assert(blockedPlan.BlockedReason!.Contains("Python or pip is not installed"), "Blocked reason must explain missing tools.");

// ============================================================================
// 7. Installation Plan Typed Model Validation
// ============================================================================
Console.WriteLine("[7/11] Testing typed installation plan models and fields...");
InstallationPlan exePlan = InstallationPlanService.ForReleaseAsset(Asset("test.exe", 100));
Assert(exePlan.ExecutesPublisherCode, "EXE plan must execute publisher code.");
Assert(exePlan.ProposedCommands.Count > 0, "Proposed commands must not be empty.");
Assert(exePlan.RequiredPermissions == RequiredPermissionLevel.StandardUser, "Permissions level mismatch.");
Assert(!string.IsNullOrWhiteSpace(exePlan.RollbackStrategy), "Rollback strategy must be specified.");
Assert(!string.IsNullOrWhiteSpace(exePlan.ExpectedVerificationProcedure), "Verification procedure must be specified.");

InstallationPlan sevenZipPlan = InstallationPlanService.ForReleaseAsset(Asset("portable.7z", 100));
Assert(!sevenZipPlan.ExecutesPublisherCode, "7z extraction must not be marked as publisher code execution.");
Assert(sevenZipPlan.Method == InstallationMethod.Portable7zArchive, "7z method mismatch.");

// ============================================================================
// 8. Explicit Confirmation Boundaries & Guarded Execution
// ============================================================================
Console.WriteLine("[8/11] Testing explicit confirmation boundaries and guarded execution...");
GuardedPlanAdapter guardedAdapter = new();
InstallationPlan dockerPlan = new()
{
    Repository = new GitHubRepository("owner", "docker-app"),
    Version = "1.0",
    Ecosystem = RepositoryEcosystem.Docker,
    Method = InstallationMethod.DockerSetupPlan,
    VerificationMethod = new(IntegrityVerificationKind.None, null, null, "None"),
    ExpectedVerificationProcedure = "docker ps",
    RollbackStrategy = "docker compose down",
    CanExecuteAutomatically = false,
    DisplayTitle = "Docker setup plan",
    Summary = "Docker setup plan only."
};

AssertThrows<InvalidOperationException>(
    () => guardedAdapter.ExecuteAsync(dockerPlan, null, false, _ => { }, CancellationToken.None).GetAwaiter().GetResult(),
    "Automated execution of Docker containers must be strictly guarded.");

// ============================================================================
// 9. Package-Manager Argument Validation (Anti-Injection)
// ============================================================================
Console.WriteLine("[9/11] Testing package-manager argument validation and anti-injection...");
WinGetAdapter wingetAdapter = new(mockAllToolsInstalled);
InstallationPlan injectionPlan = new()
{
    Repository = new GitHubRepository("owner", "--dangerous-flag"),
    Version = "1.0",
    Ecosystem = RepositoryEcosystem.WinGet,
    Method = InstallationMethod.WinGetPackage,
    VerificationMethod = new(IntegrityVerificationKind.PackageManagerVerified, null, null, "WinGet"),
    ExpectedVerificationProcedure = "exit 0",
    RollbackStrategy = "none",
    CanExecuteAutomatically = true,
    DisplayTitle = "WinGet injection test",
    Summary = "Test injection rejection."
};

AssertThrows<InvalidOperationException>(
    () => wingetAdapter.ExecuteAsync(injectionPlan, null, false, _ => { }, CancellationToken.None).GetAwaiter().GetResult(),
    "Arguments starting with flags or containing injection characters must be rejected.");

PipAdapter pipAdapter = new(mockAllToolsInstalled);
InstallationPlan pipInjection = new()
{
    Repository = new GitHubRepository("owner", "-e /malicious/path"),
    Version = "1.0",
    Ecosystem = RepositoryEcosystem.Python,
    Method = InstallationMethod.PipPackage,
    VerificationMethod = new(IntegrityVerificationKind.PackageManagerVerified, null, null, "pip"),
    ExpectedVerificationProcedure = "exit 0",
    RollbackStrategy = "none",
    CanExecuteAutomatically = true,
    DisplayTitle = "pip injection test",
    Summary = "Test injection rejection."
};

AssertThrows<InvalidOperationException>(
    () => pipAdapter.ExecuteAsync(pipInjection, null, false, _ => { }, CancellationToken.None).GetAwaiter().GetResult(),
    "pip package names starting with flags must be rejected.");

// ============================================================================
// 10. Integrity Verification and Authenticode Inspection
// ============================================================================
Console.WriteLine("[10/11] Testing Authenticode inspection and publisher checksum verification...");
string tempChecksumFile = Path.Combine(Path.GetTempPath(), "test-checksum-" + Guid.NewGuid().ToString("N") + ".bin");
try
{
    byte[] testBytes = "checksum-verification-content"u8.ToArray();
    File.WriteAllBytes(tempChecksumFile, testBytes);

    string calculatedHash = await IntegrityVerificationService.ComputeSha256Async(tempChecksumFile);
    Assert(!string.IsNullOrWhiteSpace(calculatedHash), "SHA-256 calculation failed.");

    bool checksumValid = await IntegrityVerificationService.VerifyPublisherChecksumAsync(tempChecksumFile, calculatedHash);
    Assert(checksumValid, "Publisher checksum verification should pass for identical hash.");

    await AssertThrowsAsync<InvalidOperationException>(
        () => IntegrityVerificationService.VerifyPublisherChecksumAsync(tempChecksumFile, "0000000000000000000000000000000000000000000000000000000000000000"),
        "Mismatched checksum must throw InvalidOperationException.");

    AuthenticodeResult authResult = IntegrityVerificationService.VerifyAuthenticode(tempChecksumFile);
    Assert(!authResult.IsSigned, "Plain text binary must report as unsigned.");
}
finally
{
    if (File.Exists(tempChecksumFile))
    {
        File.Delete(tempChecksumFile);
    }
}

// ============================================================================
// 11. Archive Traversal, 7z Safety, Atomic Operations, and Rollback
// ============================================================================
Console.WriteLine("[11/11] Testing archive safety, 7z extraction, atomic writes, and rollback...");
string testRoot = Path.Combine(
    Path.GetTempPath(),
    "GitHubAutoInstallerSmokeTests",
    Guid.NewGuid().ToString("N"));

try
{
    Directory.CreateDirectory(testRoot);
    await VerifyAtomicDownloadAsync(testRoot);
    await VerifyDownloadSizeMismatchAsync(testRoot);
    await VerifyZipInstallationAsync(testRoot);
    await VerifyZipTraversalIsRejectedAsync(testRoot);
    await Verify7zInstallationAsync(testRoot);
    await Verify7zTraversalIsRejectedAsync(testRoot);
    await VerifyRollbackPreservesExistingDirectoryAsync(testRoot);
}
finally
{
    if (Directory.Exists(testRoot))
    {
        Directory.Delete(testRoot, recursive: true);
    }
}

Console.WriteLine("All smoke tests passed successfully.");
return;

// ============================================================================
// Helper Verification Methods
// ============================================================================

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

static async Task VerifyDownloadSizeMismatchAsync(string testRoot)
{
    byte[] payload = "short"u8.ToArray();
    using HttpClient httpClient = new(new StaticResponseHandler(payload));
    FileDownloadService service = new(httpClient);

    await AssertThrowsAsync<InvalidOperationException>(
        () => service.DownloadAsync(
            Asset("wrong-size.zip", payload.Length + 1),
            Path.Combine(testRoot, "mismatch"),
            new Progress<double>(),
            CancellationToken.None),
        "Truncated or inconsistent download must be rejected.");
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

static async Task VerifyZipTraversalIsRejectedAsync(string testRoot)
{
    string zipPath = Path.Combine(testRoot, "unsafe.zip");
    using (ZipArchive archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
    {
        archive.CreateEntry("../outside.exe");
    }

    AssetInstallerService service = new(Path.Combine(testRoot, "unsafe-installed"));
    await AssertThrowsAsync<InvalidOperationException>(
        () => service.InstallAsync(
            zipPath,
            "unsafe-repository",
            silent: false,
            _ => { },
            CancellationToken.None),
        "ZIP path traversal entry must be rejected.");
}

static async Task Verify7zInstallationAsync(string testRoot)
{
    string sevenZipPath = Path.Combine(testRoot, "valid.7z");
    using (FileStream fs = File.Create(sevenZipPath))
    using (IWriter writer = SevenZipWriter.OpenWriter(fs, new SevenZipWriterOptions()))
    using (MemoryStream ms = new("7z-executable-content"u8.ToArray()))
    {
        writer.Write("bin/app.exe", ms, DateTime.UtcNow);
    }

    AssetInstallerService service = new(Path.Combine(testRoot, "installed-7z"));
    InstallationResult result = await service.InstallAsync(
        sevenZipPath,
        "sevenzip-repo",
        silent: false,
        _ => { },
        CancellationToken.None);

    Assert(result.ExitCode == 0, "7z install returned invalid exit code.");
    Assert(
        File.Exists(Path.Combine(result.InstalledDirectory!, "bin", "app.exe")),
        "7z install did not extract expected executable.");
}

static async Task Verify7zTraversalIsRejectedAsync(string testRoot)
{
    string unsafe7zPath = Path.Combine(testRoot, "unsafe.7z");
    using (FileStream fs = File.Create(unsafe7zPath))
    using (IWriter writer = SevenZipWriter.OpenWriter(fs, new SevenZipWriterOptions()))
    using (MemoryStream ms = new("malicious"u8.ToArray()))
    {
        writer.Write("../escaped.exe", ms, DateTime.UtcNow);
    }

    AssetInstallerService service = new(Path.Combine(testRoot, "unsafe-7z-installed"));
    await AssertThrowsAsync<InvalidOperationException>(
        () => service.InstallAsync(
            unsafe7zPath,
            "unsafe-7z-repo",
            silent: false,
            _ => { },
            CancellationToken.None),
        "7z path traversal entry must be rejected.");
}

static async Task VerifyRollbackPreservesExistingDirectoryAsync(string testRoot)
{
    string installRoot = Path.Combine(testRoot, "rollback-test");
    Directory.CreateDirectory(installRoot);
    string targetDir = Path.Combine(installRoot, "existing-app");
    Directory.CreateDirectory(targetDir);
    string originalFile = Path.Combine(targetDir, "original.txt");
    await File.WriteAllTextAsync(originalFile, "original-content");

    // Create a corrupt/unsafe ZIP that fails midway
    string badZip = Path.Combine(testRoot, "bad.zip");
    using (ZipArchive archive = ZipFile.Open(badZip, ZipArchiveMode.Create))
    {
        archive.CreateEntry("../bad-traversal.exe");
    }

    AssetInstallerService service = new(installRoot);
    await AssertThrowsAsync<InvalidOperationException>(
        () => service.InstallAsync(badZip, "existing-app", false, _ => { }, CancellationToken.None),
        "Corrupt ZIP must throw.");

    Assert(Directory.Exists(targetDir), "Original directory must be preserved after failed update.");
    Assert(File.Exists(originalFile), "Original files must be preserved after failed update.");
    Assert(await File.ReadAllTextAsync(originalFile) == "original-content", "Original content must be intact.");
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

static async Task AssertThrowsAsync<TException>(Func<Task> action, string message)
    where TException : Exception
{
    try
    {
        await action();
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
