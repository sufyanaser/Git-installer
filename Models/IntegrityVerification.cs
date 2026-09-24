namespace GitHubAutoInstaller.Models;

public enum IntegrityVerificationKind
{
    None,
    LocalSha256Audit,
    PublisherChecksum,
    AuthenticodeSignature,
    PackageManagerVerified
}

public sealed record IntegrityVerification(
    IntegrityVerificationKind Kind,
    string? ExpectedHash,
    string? Algorithm,
    string Description);
