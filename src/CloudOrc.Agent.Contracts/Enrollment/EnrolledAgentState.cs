namespace CloudOrc.Agent.Contracts.Enrollment;

/// <summary>
/// What the Control Agent persists locally (DPAPI-protected, machine scope) after a
/// successful enrollment. This - not appsettings.json - is the source of truth for
/// AgentId/ServerId/BackendUrl/credential once an agent is enrolled; it survives reboots
/// and service restarts without any manual configuration or re-enrollment.
/// </summary>
public sealed class EnrolledAgentState
{
    public required string AgentId { get; init; }

    public required string ServerId { get; init; }

    public required string BackendUrl { get; init; }

    public required string Credential { get; init; }

    public required DateTimeOffset EnrolledAtUtc { get; init; }

    /// <summary>
    /// Base URL of the HTTPS control plane (admission, grants, events, session renewal). Null for
    /// agents enrolled before backup support - it is then derived from <see cref="BackendUrl"/>.
    /// </summary>
    public string? ApiBaseUrl { get; init; }

    /// <summary>Portal URL the tray's "Open CloudOrc Portal" button opens. Optional.</summary>
    public string? PortalUrl { get; init; }

    /// <summary>When the session credential expires (null = no expiry announced by the backend).</summary>
    public DateTimeOffset? CredentialExpiresAtUtc { get; init; }

    /// <summary>Thumbprint of the mTLS client certificate in LocalMachine\My, when one was issued.</summary>
    public string? ClientCertificateThumbprint { get; init; }

    /// <summary>How this agent was enrolled: "token" (managed VPS) or "activation-code".</summary>
    public string? EnrollmentMethod { get; init; }

    /// <summary>Backend command-signing public key (base64 SPKI). When set, typed commands must be signed.</summary>
    public string? CommandSigningPublicKey { get; init; }

    /// <summary>Copy with selected values replaced - keeps every other persisted field intact.</summary>
    public EnrolledAgentState With(
        string? credential = null,
        DateTimeOffset? credentialExpiresAtUtc = null,
        bool clearCredentialExpiry = false,
        string? clientCertificateThumbprint = null) => new()
    {
        AgentId = AgentId,
        ServerId = ServerId,
        BackendUrl = BackendUrl,
        Credential = credential ?? Credential,
        EnrolledAtUtc = EnrolledAtUtc,
        ApiBaseUrl = ApiBaseUrl,
        PortalUrl = PortalUrl,
        CredentialExpiresAtUtc = clearCredentialExpiry ? null : credentialExpiresAtUtc ?? CredentialExpiresAtUtc,
        ClientCertificateThumbprint = clientCertificateThumbprint ?? ClientCertificateThumbprint,
        EnrollmentMethod = EnrollmentMethod,
        CommandSigningPublicKey = CommandSigningPublicKey
    };
}
