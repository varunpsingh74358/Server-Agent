using System.Text.Json.Serialization;

namespace CloudOrc.Agent.Contracts.Protocol;

/// <summary>
/// Backend -> Agent: a command envelope. <see cref="CommandType"/> selects which executor
/// handles <see cref="Parameters"/> - today only "powershell-exec" exists, but the shape
/// leaves room for a future distinct <c>type</c> (e.g. FILE_UPLOAD) rather than growing
/// this one envelope with unrelated parameter shapes.
/// </summary>
public sealed class CommandMessage
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = ProtocolMessageTypes.Command;

    [JsonPropertyName("commandId")]
    public required string CommandId { get; init; }

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; init; }

    [JsonPropertyName("commandType")]
    public string CommandType { get; init; } = "powershell-exec";

    [JsonPropertyName("createdAt")]
    public DateTimeOffset? CreatedAt { get; init; }

    [JsonPropertyName("parameters")]
    public required CommandParameters Parameters { get; init; }
}

/// <summary>Parameters for the "powershell-exec" <see cref="CommandMessage.CommandType"/>.</summary>
public sealed class CommandParameters
{
    [JsonPropertyName("script")]
    public required string Script { get; init; }

    [JsonPropertyName("timeoutSeconds")]
    public int? TimeoutSeconds { get; init; }
}
