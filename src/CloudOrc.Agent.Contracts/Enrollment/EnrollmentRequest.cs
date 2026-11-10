using System.Text.Json.Serialization;

namespace CloudOrc.Agent.Contracts.Enrollment;

/// <summary>
/// Sent by the Control Agent (via its <c>enroll</c> CLI mode) to the enrollment endpoint
/// decoded from the token. The <see cref="Secret"/> is the single-use bootstrap value the
/// backend validates and consumes - it is never the agent's permanent credential.
/// </summary>
public sealed class EnrollmentRequest
{
    [JsonPropertyName("secret")]
    public required string Secret { get; init; }

    [JsonPropertyName("machineId")]
    public required string MachineId { get; init; }

    [JsonPropertyName("machineName")]
    public required string MachineName { get; init; }

    [JsonPropertyName("agentVersion")]
    public required string AgentVersion { get; init; }

    /// <summary>Optional: random per-install id for duplicate/reimage detection (§4). Ignored by older backends.</summary>
    [JsonPropertyName("installationId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InstallationId { get; init; }

    /// <summary>Optional: device public key (base64 SPKI) for later session-renewal proofs (§4, §12).</summary>
    [JsonPropertyName("devicePublicKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DevicePublicKey { get; init; }

    /// <summary>Optional: CSR (PEM) for an mTLS client certificate.</summary>
    [JsonPropertyName("csrPem")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CsrPem { get; init; }
}
