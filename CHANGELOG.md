# Changelog

## 1.4.0 - 2026-10-01

- Eliminate all native Win32 `MessageBox.Show` dialog popups across the application, replacing them with a unified in-app dark modal overlay (`ModalOverlay`) for alerts, completions, errors, and cancellation prompts.
- Implement non-blocking in-app toast notification card (`ToastCard`) for status confirmations (empty clipboard alerts, log copy confirmations, up-to-date checks).
- Remove blocking confirmation modal prior to download execution, enabling instant one-click deployment workflows upon reviewing plan details.
- Provide synchronized, real-time progress tracking:
  - Byte-accurate download progress displaying live percentage and formatted data size (e.g. `18.2 MB / 42.5 MB`).
  - Active high-resolution installation duration ticker (`DispatcherTimer` + `Stopwatch`) tracking elapsed time (`(mm:ss)`) with asymptotic progress advancement during setup execution.
  - Silky smooth progress bar transitions using WPF `DoubleAnimation` and vibrant linear gradient styling.
- Enhance UI aesthetics:
  - Deep dark background depth gradient and refined console card elevation drop shadows.
  - Hover glow micro-interactions on primary action buttons.
  - Modern vector-styled checkboxes with smooth active transitions.
  - Pulsing status indicator dot during active inspection, installation, and auto-update operations.
  - Sleek developer console styling for the activity log.

## 1.3.0 - 2026-10-01

- Fix release installation method dropdown menu styling in dark mode: introduce dedicated modern WPF ControlTemplate for `ComboBox` and `ComboBoxItem` with dark surface background (`#141C26`), subtle elevation drop-shadow, hit-tested background bindings, `StaysOpen=false` popup behavior, and high-contrast text to eliminate the white rectangle visual bug.
- Introduce automated background update reception service (`AutoUpdateService`) that checks the self-repository (`sufyanaser/Git-installer`) for new releases/pre-releases on startup and periodically every 30 minutes, automatically downloading and verifying update payloads in the background.
- Add prominent Update Notification Banner in MainWindow showing real-time background reception progress and instant "Restart to Apply" handoff with automatic process relaunch for both Inno Setup installers and standalone binaries.
- Add manual "Check updates" action in the sidebar with live activity logging and status dialogs.
- Support automated update download, integrity verification against publisher `SHA256SUMS.txt`, architecture normalization for `x86_64` assets, and seamless in-place restart handoff.
- Harden `release.yml` GitHub Actions workflow with full `workflow_dispatch` support for automated release generation upon repository development.
- Add unit and smoke test coverage for version comparison, asset scoring, checksum parsing, multi-release array discovery, and WPF ComboBox dark template verification.

## 1.2.1 - 2026-09-27

- Support GitHub repository URLs with subpaths (e.g. `/releases`, `/releases/latest`, `/tree/...`, `/tags`).
- Add GitHub API authorization token support via `GITHUB_TOKEN` and `GH_TOKEN` environment variables for 5,000 req/hr limits.
- Gracefully handle releases without binary assets, preventing unhandled exceptions and allowing package-manager fallback options.
- Support Windows reboot exit codes (1641 and 3010) in WinGet adapter without false-positive failures.
- Resolve external executables against system and user PATH before execution in `ProcessExecutionService`.
- Harden `DesktopShortcutService` with robust `EnumerationOptions`, filename sanitization, and exception guards.
- Safely handle process tree termination during operation cancellation in `AssetInstallerService`.
- Improve keyboard accessibility by supporting the Enter key in the repository URL input field.
- Package all required dependencies, satellite assemblies, and runtime assets in Inno Setup installer.

## 1.2.0 - 2026-09-24

- Add repository-aware inspection for GitHub releases, WinGet manifests, Python, Node.js, Rust, .NET, Docker, and root installation scripts.
- Introduce typed installation plans detailing exact proposed commands, target architecture, required permissions, prerequisites, rollback strategies, and security warnings.
- Provide multi-option presentation when multiple valid installation methods exist, prioritizing official Windows release assets.
- Add safe 7z portable archive extraction with traversal protection, symlink rejection, 10,000 entry limit, and 4 GB size limits.
- Implement controlled package manager adapters for WinGet, pip, npm, cargo, and dotnet tool with strict argument validation against injection.
- Add tool verification service for non-invasive dependency checking before proposing or running package manager commands.
- Implement Windows Authenticode signature verification and publisher checksum matching.
- Add structured process execution using argument lists to avoid command and shell injection.
- Implement explicit confirmation boundary in WPF UI before initiating downloads or executing installation commands.
- Provide actionable explanations and guarded setup plans for unsupported repositories, libraries, and Docker services.
- Extend smoke-test suite covering manifest detection, conflicting methods, unsupported types, architecture mismatch, missing dependencies, plan models, confirmation boundaries, archive safety, argument validation, and rollback.

## 1.1.0 - 2026-09-22

- Explain releases with no uploaded assets or incompatible package formats.
- Show the inspected release and its URL when no Windows x64 asset can be installed.
- Enforce HTTPS, declared download size, and a 2 GB download ceiling.
- Harden ZIP extraction against traversal, symbolic links, unsafe names, entry floods, and oversized expansion.
- Add CI build, formatting, smoke-test, and publish verification.
- Add project usage, limitations, build, and security documentation.
- Add an automated, checksummed GitHub Release pipeline.

## 1.0.1 - 2026-08-02

- Add guarded support for PowerShell release assets.

## 1.0.0 - 2026-08-02

- Add resilient release discovery, safer downloads and installation, the desktop interface, smoke tests, and reproducible installer packaging.
