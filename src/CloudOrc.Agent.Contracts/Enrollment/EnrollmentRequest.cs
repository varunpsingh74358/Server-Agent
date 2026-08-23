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
}
