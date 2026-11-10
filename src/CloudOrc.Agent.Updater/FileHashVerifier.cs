using System.Security.Cryptography;

namespace CloudOrc.Agent.Updater;

public sealed class FileHashVerifier : IFileHashVerifier
{
    public VerificationResult Verify(string filePath, string expectedSha256Hex)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return VerificationResult.Invalid($"Installer not found at '{filePath}'.");
            }

            var actual = ComputeSha256Hex(filePath);
            return string.Equals(actual, expectedSha256Hex.Trim(), StringComparison.OrdinalIgnoreCase)
                ? VerificationResult.Valid()
                : VerificationResult.Invalid($"SHA-256 mismatch: expected {expectedSha256Hex.Trim().ToLowerInvariant()}, actual {actual}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return VerificationResult.Invalid($"Could not read installer for hashing: {ex.Message}");
        }
    }

    public static string ComputeSha256Hex(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
