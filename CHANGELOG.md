# Changelog

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
