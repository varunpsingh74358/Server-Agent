using System.Text.Json.Serialization;

namespace CloudOrc.Agent.Contracts.Enrollment;

/// <summary>
/// Returned by the enrollment endpoint on success. This is the ONLY place the agent ever
/// learns its backend connection details - nothing about <see cref="BackendUrl"/> is ever
/// hardcoded, manually configured, or baked into the Agent binary/installer.
/// </summary>
public sealed class EnrollmentResponse
{
    [JsonPropertyName("agentId")]
    public required string AgentId { get; init; }

    [JsonPropertyName("serverId")]
    public required string ServerId { get; init; }

    /// <summary>The real-time backend WebSocket endpoint this agent should connect to.</summary>
    [JsonPropertyName("backendUrl")]
    public required string BackendUrl { get; init; }

    /// <summary>
    /// Permanent per-agent authentication credential (presented as a bearer credential on
    /// the WebSocket handshake) - distinct from, and never derived from, the one-time
    /// enrollment secret that was just consumed to obtain it.
    /// </summary>
    [JsonPropertyName("credential")]
    public required string Credential { get; init; }

    /// <summary>Optional: HTTPS control-plane base URL. Derived from BackendUrl when absent.</summary>
    [JsonPropertyName("apiBaseUrl")]
    public string? ApiBaseUrl { get; init; }

    /// <summary>Optional: portal URL for the local status UI.</summary>
    [JsonPropertyName("portalUrl")]
    public string? PortalUrl { get; init; }

    /// <summary>Optional: credential expiry for short-lived session credentials (§10).</summary>
    [JsonPropertyName("credentialExpiresAt")]
    public DateTimeOffset? CredentialExpiresAt { get; init; }

    /// <summary>Optional: PEM client certificate issued for the CSR sent at enrollment (mTLS, §3/§10).</summary>
    [JsonPropertyName("clientCertificatePem")]
    public string? ClientCertificatePem { get; init; }

    /// <summary>
    /// Optional: base64 SPKI of the backend's ECDSA P-256 command-signing key. Once an agent has it,
    /// every typed command must carry a valid signature (§10).
    /// </summary>
    [JsonPropertyName("commandSigningPublicKey")]
    public string? CommandSigningPublicKey { get; init; }
}
