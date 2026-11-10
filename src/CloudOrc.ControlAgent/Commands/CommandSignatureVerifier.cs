using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudOrc.Agent.Contracts.Backup;

namespace CloudOrc.ControlAgent.Commands;

public sealed record SignatureCheck(bool Ok, string? ReasonCode, string? Message)
{
    public static readonly SignatureCheck Valid = new(true, null, null);
}

/// <summary>
/// §10 "All commands typed, signed/authenticated". The WebSocket already authenticates the
/// backend (TLS) and the agent (credential / mTLS); this adds end-to-end integrity: a typed
/// command is only executed if the backend's command-signing key signed exactly this command id,
/// type, version, issue time and parameter bytes. A compromised relay, proxy or gateway node can
/// therefore not forge or alter a restore/update/policy command.
///
/// Enforced as soon as the agent holds the backend's public key (delivered at enrollment), or
/// always when <c>requireSignatures</c> is configured. Replay is bounded by issuedAt (±window)
/// on top of the durable inbox's commandId de-duplication.
/// </summary>
public sealed class CommandSignatureVerifier
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    private readonly ECDsa? _key;
    private readonly bool _required;
    private readonly TimeProvider _time;

    public CommandSignatureVerifier(string? publicKeySpkiBase64, bool requireSignatures, TimeProvider time)
    {
        _time = time;
        _required = requireSignatures || !string.IsNullOrWhiteSpace(publicKeySpkiBase64);
        if (!string.IsNullOrWhiteSpace(publicKeySpkiBase64))
        {
            try
            {
                var key = ECDsa.Create();
                key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeySpkiBase64), out _);
                _key = key;
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException)
            {
                _key = null; // fail closed below: required but unusable key
            }
        }
    }

    public bool IsEnforced => _required;

    public SignatureCheck Verify(string rawJson)
    {
        if (!_required)
        {
            return SignatureCheck.Valid;
        }

        if (_key is null)
        {
            return new SignatureCheck(false, "SIGNATURE_KEY_UNAVAILABLE", "Command signatures are required but no valid backend signing key is configured.");
        }

        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;
        string? Get(string name) => root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

        var signature = Get("signature");
        var issuedAtText = Get("issuedAt");
        if (string.IsNullOrEmpty(signature) || issuedAtText is null || !DateTimeOffset.TryParse(issuedAtText, out var issuedAt))
        {
            return new SignatureCheck(false, "SIGNATURE_MISSING", "This command is not signed.");
        }

        var now = _time.GetUtcNow();
        if (issuedAt > now + MaxClockSkew || now - issuedAt > MaxAge)
        {
            return new SignatureCheck(false, "SIGNATURE_EXPIRED", "This command's signature is outside the accepted time window.");
        }

        // The backend stores commands in jsonb (which reorders keys), so it ships the exact signed
        // text as the string "parametersJson"; when present that string is what is hashed (and what
        // TypedCommandContext.Parameters<T>() deserializes), never the re-serialized "parameters".
        var parameters = root.TryGetProperty("parametersJson", out var pj) && pj.ValueKind == JsonValueKind.String
            ? pj.GetString()!
            : root.TryGetProperty("parameters", out var p) ? p.GetRawText() : "null";
        var payload = CommandSigning.BuildPayload(
            Get("commandId") ?? "",
            Get("commandType") ?? "",
            Get("commandVersion") ?? BackupSchema.Version,
            issuedAt,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(parameters))));

        byte[] signatureBytes;
        try
        {
            signatureBytes = Convert.FromBase64String(signature);
        }
        catch (FormatException)
        {
            return new SignatureCheck(false, "SIGNATURE_INVALID", "The command signature is malformed.");
        }

        return _key.VerifyData(Encoding.UTF8.GetBytes(payload), signatureBytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)
            ? SignatureCheck.Valid
            : new SignatureCheck(false, "SIGNATURE_INVALID", "The command signature does not match - command rejected.");
    }
}
