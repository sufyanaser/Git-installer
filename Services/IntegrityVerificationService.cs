using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using GitHubAutoInstaller.Models;

namespace GitHubAutoInstaller.Services;

public sealed record AuthenticodeResult(
    bool IsSigned,
    string? Subject,
    string? Issuer,
    string Details);

/// <summary>
/// Verifies digital signatures and publisher checksums.
/// Never presents a locally computed SHA-256 hash as proof of publisher authenticity.
/// </summary>
public static class IntegrityVerificationService
{
    public static AuthenticodeResult VerifyAuthenticode(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
        {
            return new AuthenticodeResult(false, null, null, "File does not exist.");
        }

        try
        {
#pragma warning disable SYSLIB0057
            using X509Certificate? cert = X509Certificate.CreateFromSignedFile(filePath);
#pragma warning restore SYSLIB0057
            if (cert is not null)
            {
                using X509Certificate2 cert2 = new(cert);
                return new AuthenticodeResult(
                    IsSigned: true,
                    Subject: cert2.Subject,
                    Issuer: cert2.Issuer,
                    Details: $"Signed by: {cert2.GetNameInfo(X509NameType.SimpleName, forIssuer: false)}, Issued by: {cert2.GetNameInfo(X509NameType.SimpleName, forIssuer: true)}");
            }
        }
        catch (CryptographicException)
        {
            // File is not digitally signed or signature is malformed
        }
        catch
        {
            // Format not supported for Authenticode inspection
        }

        return new AuthenticodeResult(
            IsSigned: false,
            Subject: null,
            Issuer: null,
            Details: "File is not digitally signed with a Windows Authenticode signature.");
    }

    public static async Task<string> ComputeSha256Async(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        await using FileStream stream = new(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81_920,
            useAsync: true);

        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    public static async Task<bool> VerifyPublisherChecksumAsync(
        string filePath,
        string expectedHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedHash);

        string cleanExpected = expectedHash.Trim().ToLowerInvariant();
        string computed = (await ComputeSha256Async(filePath, cancellationToken)).ToLowerInvariant();

        if (!computed.Equals(cleanExpected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Integrity verification failed! Downloaded file SHA-256 ({computed}) does not match the publisher's declared checksum ({cleanExpected}). The file may be corrupt or tampered with.");
        }

        return true;
    }
}
