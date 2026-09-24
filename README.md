# GitHub Auto Installer

A Repository-Aware Windows desktop utility that inspects public GitHub repositories, classifies their available installation methods and dependencies, presents a typed installation plan, and installs verified applications with explicit user confirmation.

## Supported deployment methods

### 1. Official Windows Releases
- `.msi` Windows Installer packages
- `.exe` Windows standalone installers
- `.zip` portable application archives
- `.7z` high-compression portable archives
- `.ps1` PowerShell installation scripts (executed in an isolated, `-NoLogo -NoProfile` session after explicit approval)

### 2. Controlled Package Manager Integrations
When an official Windows release asset is not present or when alternative installation methods exist, the application inspects repository manifests and can generate installation plans for:
- **WinGet**: Windows Package Manager manifests (`winget.yaml`, `winget-pkgs.yaml`)
- **pip**: Python CLI applications (`pyproject.toml`, `setup.py`, `requirements.txt`)
- **npm**: Node.js global CLI applications (`package.json`)
- **cargo**: Rust binary crates (`Cargo.toml`)
- **dotnet**: .NET global tools (`.csproj`, `.sln`)

### 3. Guarded & Unsupported Repositories
- **Docker projects**: Repositories containing `Dockerfile` or `compose.yaml` receive an inspectable setup plan with proposed commands. Automated container launching is intentionally guarded to prevent unreviewed background system modifications.
- **Libraries & Development Packages**: Class libraries and development modules intended for import rather than standalone execution are detected and classified as `PackageOrLibrary`, with actionable explanations provided to the user.
- **Ambiguous or Source-Only Repositories**: Repositories lacking automated installers or recognized manifests are marked as unsupported with clear technical explanations rather than attempting speculative execution.

## Installation workflow

```
Repository URL
    ↓
Inspect repository (metadata, releases, manifests)
    ↓
Identify available installation methods & prioritize official releases
    ↓
Check Windows x64 compatibility
    ↓
Inspect local tool dependencies (WinGet, Python, Node, Cargo, .NET)
    ↓
Display typed installation plan (exact commands, verification, rollback, warnings)
    ↓
Explicit user confirmation (Yes / No)
    ↓
Download & verify (Authenticode signature / publisher checksum)
    ↓
Execute installation adapter
    ↓
Verify result (process exit code & filesystem check)
```

## Security model

- **Explicit Confirmation Boundary**: The application never executes downloaded code or runs package manager commands without explicit user review and confirmation of the installation plan.
- **Strict HTTPS Only**: Only HTTPS GitHub repository URLs and HTTPS download sources are accepted.
- **Download Limits & Atomic Writes**: Downloads are capped at a 2 GB safety ceiling and written atomically using temporary files checked against declared content lengths.
- **Safe Archive Extraction**: ZIP and 7z extractions enforce directory traversal rejection (Zip-Slip defense), symbolic link blocking, a 10,000 entry ceiling, and a 4 GB uncompressed expansion ceiling.
- **Atomic Rollback**: Portable archive installations stage new files in a temporary location and preserve a backup copy of existing installations, automatically rolling back if an extraction fails.
- **Integrity & Authenticode Verification**: Inspects Windows Authenticode digital signatures on executable binaries and verifies publisher checksums when available. Locally computed SHA-256 hashes are clearly identified as audit records rather than proof of publisher authenticity.
- **Command Injection Prevention**: All external commands are executed using structured argument lists (`ProcessStartInfo.ArgumentList`), strictly validating package identifiers and preventing shell or flag injection.

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

The Inno Setup definition in `installer/GitHubAutoInstaller.iss` packages the published executable.
