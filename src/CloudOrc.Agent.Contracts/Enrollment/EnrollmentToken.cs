using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudOrc.Agent.Contracts.Enrollment;

/// <summary>
/// The decoded payload of an enrollment token: where to redeem it, and the single-use
/// secret to redeem with. Never contains the backend's data-plane URL - only the
/// enrollment/bootstrap endpoint, which the enrollment response then uses to hand back the
/// actual backend connection details. See docs/ENROLLMENT.md for the full design and why
/// this shape was chosen over a fixed, hardcoded bootstrap host.
/// </summary>
public sealed class DecodedEnrollmentToken
{
    [JsonPropertyName("u")]
    public required string EnrollmentUrl { get; init; }

    [JsonPropertyName("s")]
    public required string Secret { get; init; }
}

/// <summary>
/// Encodes/decodes the opaque <c>ENR-...</c> enrollment token string. The token is a
/// single, copy-pasteable value an administrator supplies once at install time - it is
/// never manually constructed or parsed by a human, and never requires them to know or
/// type a URL. Internally it is a base64url-encoded JSON payload; this is NOT encryption
/// (anyone holding the token string can trivially decode it) - the token's security comes
/// entirely from <see cref="DecodedEnrollmentToken.Secret"/> being a single-use,
/// short-lived, backend-validated value, not from the encoding being opaque to inspection.
/// </summary>
public static class EnrollmentToken
{
    private const string Prefix = "ENR-";

    public static string Encode(string enrollmentUrl, string secret)
    {
        if (string.IsNullOrWhiteSpace(enrollmentUrl))
        {
            throw new ArgumentException("Enrollment URL must not be empty.", nameof(enrollmentUrl));
        }

        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new ArgumentException("Secret must not be empty.", nameof(secret));
        }

        var payload = new DecodedEnrollmentToken { EnrollmentUrl = enrollmentUrl, Secret = secret };
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        return Prefix + Base64UrlEncode(bytes);
    }

    /// <summary>
    /// Decodes a token string. Returns false (never throws) for anything malformed, so
    /// callers can surface a clean "invalid token" error instead of an unhandled exception
    /// - the token originates outside this process (typed/pasted by an operator), so it
    /// must be treated as untrusted input.
    /// </summary>
    public static bool TryDecode(string? token, out DecodedEnrollmentToken? decoded)
    {
        decoded = null;

        if (string.IsNullOrWhiteSpace(token) || !token.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var base64 = token[Prefix.Length..];
            var bytes = Base64UrlDecode(base64);
            var json = Encoding.UTF8.GetString(bytes);
            var payload = JsonSerializer.Deserialize<DecodedEnrollmentToken>(json);

            if (payload is null
                || string.IsNullOrWhiteSpace(payload.EnrollmentUrl)
                || string.IsNullOrWhiteSpace(payload.Secret)
                || !Uri.TryCreate(payload.EnrollmentUrl, UriKind.Absolute, out _))
            {
                return false;
            }

            decoded = payload;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException or DecoderFallbackException)
        {
            return false;
        }
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string base64Url)
    {
        var padded = base64Url.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }
}
