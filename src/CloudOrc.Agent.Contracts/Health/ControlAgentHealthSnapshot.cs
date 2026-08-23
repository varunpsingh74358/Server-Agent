using System.Text.Json.Serialization;
using CloudOrc.Agent.Contracts.Commands;

namespace CloudOrc.Agent.Contracts.Health;

/// <summary>
/// Point-in-time health of the Control Agent, served locally over a named pipe. This is
/// deliberately richer than "process exists" - it reports whether each internal worker
/// loop is actually iterating, not just that the OS process is alive.
/// </summary>
public sealed class ControlAgentHealthSnapshot
{
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("detectionWorkerAlive")]
    public required bool DetectionWorkerAlive { get; init; }

    [JsonPropertyName("processingWorkerAlive")]
    public required bool ProcessingWorkerAlive { get; init; }

    [JsonPropertyName("lastDetectionActivityAt")]
    public required DateTimeOffset LastDetectionActivityAt { get; init; }

    [JsonPropertyName("lastProcessingActivityAt")]
    public required DateTimeOffset LastProcessingActivityAt { get; init; }

    [JsonPropertyName("currentCommandId")]
    public string? CurrentCommandId { get; init; }

    [JsonPropertyName("currentCommandStatus")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CommandStatus? CurrentCommandStatus { get; init; }

    [JsonPropertyName("processedCount")]
    public required long ProcessedCount { get; init; }

    [JsonPropertyName("failedCount")]
    public required long FailedCount { get; init; }

    /// <summary>
    /// Informational only - see <see cref="BackendConnectionState"/>. The Watchdog must
    /// never use this field alone to decide whether the Control Agent is healthy.
    /// </summary>
    [JsonPropertyName("backendConnectionState")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public BackendConnectionState BackendConnectionState { get; init; } = BackendConnectionState.Disabled;

    [JsonPropertyName("generatedAt")]
    public required DateTimeOffset GeneratedAt { get; init; }
}
