using System.Collections.Concurrent;
using System.Security.Cryptography;
using CloudOrc.Agent.Contracts.Enrollment;

namespace CloudOrc.AgentTestServer.Enrollment;

/// <summary>
/// DEVELOPMENT/TEST ONLY reference implementation of enrollment-token bookkeeping: issue,
/// validate-and-consume (single use), expire, and revoke. A real backend would persist
/// this in a database with the secret stored hashed, exactly as here - only in memory
/// instead, since this tool's whole state is meant to reset when it restarts.
/// </summary>
public sealed class EnrollmentTokenStore
{
    private sealed class TokenRecord
    {
        public required DateTimeOffset ExpiresAtUtc { get; init; }
        public bool Used { get; set; }
        public bool Revoked { get; set; }
        private readonly object _gate = new();

        public object Gate => _gate;
    }

    private readonly ConcurrentDictionary<string, TokenRecord> _tokensBySecretHash = new();

    /// <summary>Issues a new single-use token that redeems at <paramref name="enrollmentUrl"/>.</summary>
    public string IssueToken(string enrollmentUrl, TimeSpan validFor)
    {
        var secret = GenerateSecret();
        var hash = Hash(secret);
        _tokensBySecretHash[hash] = new TokenRecord { ExpiresAtUtc = DateTimeOffset.UtcNow.Add(validFor) };
        return EnrollmentToken.Encode(enrollmentUrl, secret);
    }

    /// <summary>
    /// Revokes a token (identified by its full <c>ENR-...</c> string) before it has been
    /// used. Returns false if the token is unrecognized or already used/expired/revoked.
    /// </summary>
    public bool RevokeByToken(string fullToken)
    {
        if (!EnrollmentToken.TryDecode(fullToken, out var decoded))
        {
            return false;
        }

        var hash = Hash(decoded!.Secret);
        if (!_tokensBySecretHash.TryGetValue(hash, out var record))
        {
            return false;
        }

        lock (record.Gate)
        {
            if (record.Used)
            {
                return false;
            }

            record.Revoked = true;
            return true;
        }
    }

    /// <summary>
    /// Validates and, on success, atomically consumes (marks used) the secret from a
    /// redeemed token. Thread-safe against concurrent redemption attempts of the SAME
    /// secret - only one caller ever observes a successful validation for a given token,
    /// which is exactly the "enrollment token cannot be reused" / "concurrent enrollment
    /// attempts" guarantee this exists to provide.
    /// </summary>
    public EnrollmentTokenValidation ValidateAndConsume(string secret)
    {
        var hash = Hash(secret);
        if (!_tokensBySecretHash.TryGetValue(hash, out var record))
        {
            return EnrollmentTokenValidation.Invalid("Unknown enrollment token.");
        }

        lock (record.Gate)
        {
            if (record.Revoked)
            {
                return EnrollmentTokenValidation.Invalid("Enrollment token has been revoked.");
            }

            if (record.Used)
            {
                return EnrollmentTokenValidation.Invalid("Enrollment token has already been used.");
            }

            if (record.ExpiresAtUtc < DateTimeOffset.UtcNow)
            {
                return EnrollmentTokenValidation.Invalid("Enrollment token has expired.");
            }

            record.Used = true;
            return EnrollmentTokenValidation.Valid();
        }
    }

    private static string GenerateSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    internal static string Hash(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }
}

public sealed class EnrollmentTokenValidation
{
    private EnrollmentTokenValidation(bool isValid, string? error)
    {
        IsValid = isValid;
        Error = error;
    }

    public bool IsValid { get; }
    public string? Error { get; }

    public static EnrollmentTokenValidation Valid() => new(true, null);
    public static EnrollmentTokenValidation Invalid(string error) => new(false, error);
}
