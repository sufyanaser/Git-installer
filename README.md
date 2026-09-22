# GitHub Auto Installer

A Windows x64 desktop utility that inspects the latest published GitHub release, selects a compatible release asset, asks for confirmation, and then downloads and installs or extracts it.

## Supported assets

- `.msi` and `.exe` installers
- `.zip` portable applications
- `.ps1` scripts, with an explicit security warning before execution

The repository must have a published GitHub Release with an uploaded Windows x64 asset. GitHub's automatically generated source archives are not release assets and are not treated as installers. Language packages such as `.whl`, `.nupkg`, or `.tar.gz` require their own package manager and are intentionally unsupported.

For example, `microsoft/markitdown` publishes Python packages rather than a Windows installer. Install it using the method documented by that project; this application will report that no compatible release asset exists.

## Build and verify

Requirements: Windows and the .NET 10 SDK.

```powershell
dotnet restore
dotnet format --no-restore --verify-no-changes
dotnet build --no-restore --configuration Release
dotnet run --no-build --configuration Release --project GitHubAutoInstaller.SmokeTests/GitHubAutoInstaller.SmokeTests.csproj
```

Publish a self-contained executable:

```powershell
dotnet publish GitHubAutoInstaller.csproj --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/publish
```

The Inno Setup definition in `installer/GitHubAutoInstaller.iss` can package the published executable.

## Security model

Only public GitHub repositories and HTTPS release URLs are accepted. Downloads are written atomically, checked against GitHub's declared size, and limited to 2 GB. ZIP extraction rejects traversal paths, symbolic links, unsafe names, excessive entries, and expansion beyond 4 GB. The SHA-256 hash is logged for auditing, but it is not proof of publisher authenticity unless compared with a trusted publisher checksum.
