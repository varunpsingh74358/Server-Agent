using CloudOrc.Agent.Contracts.Identity;
using CloudOrc.ControlAgent.Enrollment;

namespace CloudOrc.ControlAgent.Transport;

/// <summary>
/// Single source of the CURRENT session credential for both the WebSocket and the HTTPS
/// control plane. Unlike <see cref="AgentIdentity.Credential"/> (fixed at startup), this can be
/// replaced at runtime by a session renewal (§8 "401 renewal", §10 "short-lived session
/// credentials") and the new value is persisted DPAPI-protected through <see cref="EnrolledStateStore"/>.
/// Never log <see cref="Current"/>.
/// </summary>
public sealed class AgentCredentialProvider
{
    private readonly EnrolledStateStore _store;
    private readonly Lock _sync = new();
    private string? _credential;
    private DateTimeOffset? _expiresAt;

    public AgentCredentialProvider(AgentIdentity identity, EnrolledStateStore store)
    {
        _store = store;
        _credential = identity.Credential;
        _expiresAt = store.TryLoad()?.CredentialExpiresAtUtc;
    }

    public string? Current
    {
        get
        {
            lock (_sync)
            {
                return _credential;
            }
        }
    }

    public DateTimeOffset? ExpiresAt
    {
        get
        {
            lock (_sync)
            {
                return _expiresAt;
            }
        }
    }

    public event Action? CredentialChanged;

    /// <summary>Atomically swaps in a renewed credential and persists it.</summary>
    public void Replace(string credential, DateTimeOffset? expiresAt)
    {
        if (string.IsNullOrWhiteSpace(credential))
        {
            throw new ArgumentException("Credential must not be empty.", nameof(credential));
        }

        var state = _store.TryLoad()
                    ?? throw new InvalidOperationException("Cannot renew a credential for an agent that is not enrolled.");

        _store.Save(state.With(credential: credential, credentialExpiresAtUtc: expiresAt, clearCredentialExpiry: expiresAt is null));

        lock (_sync)
        {
            _credential = credential;
            _expiresAt = expiresAt;
        }

        CredentialChanged?.Invoke();
    }
}
