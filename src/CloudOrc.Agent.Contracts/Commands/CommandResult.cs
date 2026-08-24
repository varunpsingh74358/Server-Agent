using System.Text.Json.Serialization;

namespace CloudOrc.Agent.Contracts.Commands;

/// <summary>
/// The outcome of executing one command. Written verbatim to the result sink -
/// today a JSON file, in the future a WebSocket message back to the backend.
/// </summary>
public sealed class CommandResult
{
    [JsonPropertyName("commandId")]
    public required string CommandId { get; init; }

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; init; }

    [JsonPropertyName("status")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required CommandStatus Status { get; init; }

    [JsonPropertyName("startedAt")]
    public DateTimeOffset StartedAt { get; init; }

    [JsonPropertyName("completedAt")]
    public DateTimeOffset CompletedAt { get; init; }

    [JsonPropertyName("durationMilliseconds")]
    public long DurationMilliseconds { get; init; }

    [JsonPropertyName("output")]
    public IReadOnlyList<string> Output { get; init; } = [];

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("exitCode")]
    public int? ExitCode { get; init; }
}
