using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace CloudOrc.AgentTestServer.Enrollment;

/// <summary>
/// DEVELOPMENT/TEST ONLY reference implementation of permanent per-agent credentials
/// (issued once per successful enrollment, presented as a bearer credential on every
/// subsequent WebSocket handshake). Stored hashed, same as the enrollment token secrets -
/// never in plaintext, never logged.
/// </summary>
public sealed class CredentialStore
{
    private readonly ConcurrentDictionary<string, string> _agentIdByCredentialHash = new();
    private readonly HashSet<string> _revokedCredentialHashes = new();
    private readonly object _revokeGate = new();

    public string IssueCredential(string agentId)
    {
        var credential = GenerateCredential();
        _agentIdByCredentialHash[EnrollmentTokenStore.Hash(credential)] = agentId;
        return credential;
    }

    public bool IsValid(string credential)
    {
        var hash = EnrollmentTokenStore.Hash(credential);
        lock (_revokeGate)
        {
            if (_revokedCredentialHashes.Contains(hash))
            {
                return false;
            }
        }

        return _agentIdByCredentialHash.ContainsKey(hash);
    }

    /// <summary>Revokes a previously-issued credential, e.g. to test "revoked Agent cannot authenticate".</summary>
    public bool Revoke(string credential)
    {
        var hash = EnrollmentTokenStore.Hash(credential);
        if (!_agentIdByCredentialHash.ContainsKey(hash))
        {
            return false;
        }

        lock (_revokeGate)
        {
            return _revokedCredentialHashes.Add(hash);
        }
    }

    private static string GenerateCredential()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
