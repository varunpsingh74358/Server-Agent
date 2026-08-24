using System.Text.Json.Serialization;

namespace CloudOrc.Agent.Contracts.Commands;

/// <summary>
/// The transport-agnostic representation of a command to execute.
/// This is the same shape whether it arrives as a local JSON file today
/// or over a secure WebSocket in the future - only ICommandSource
/// implementations change, never this model.
/// </summary>
public sealed class CommandRequest
{
    [JsonPropertyName("commandId")]
    public string CommandId { get; init; } = string.Empty;

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; init; }

    [JsonPropertyName("commandType")]
    public string CommandType { get; init; } = "powershell-exec";

    [JsonPropertyName("script")]
    public string Script { get; init; } = string.Empty;

    [JsonPropertyName("timeoutSeconds")]
    public int? TimeoutSeconds { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset? CreatedAt { get; init; }
}
