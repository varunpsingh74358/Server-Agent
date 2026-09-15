using System.Text.Json.Serialization;

namespace CloudOrc.Agent.Contracts.Protocol;

/// <summary>
/// Backend -> Agent: a command envelope. <see cref="CommandType"/> selects which executor
/// handles <see cref="Parameters"/> - CommandTypes.PowerShellExec and
/// CommandTypes.RdpTransferPolicyApply today, with the shape leaving room for a future
/// distinct <c>type</c> (e.g. FILE_UPLOAD) rather than growing this one envelope with
/// unrelated parameter shapes.
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

/// <summary>
/// Parameters carried by a <see cref="CommandMessage"/>. <see cref="Script"/> is used by
/// CommandTypes.PowerShellExec; <see cref="ClientToServerClipboard"/>/<see cref="ServerToClientClipboard"/>/
/// <see cref="DriveRedirection"/> are used by CommandTypes.RdpTransferPolicyApply instead -
/// a command only ever populates the fields its own commandType defines.
/// </summary>
public sealed class CommandParameters
{
    [JsonPropertyName("script")]
    public string Script { get; init; } = string.Empty;

    [JsonPropertyName("timeoutSeconds")]
    public int? TimeoutSeconds { get; init; }

    [JsonPropertyName("clientToServerClipboard")]
    public string? ClientToServerClipboard { get; init; }

    [JsonPropertyName("serverToClientClipboard")]
    public string? ServerToClientClipboard { get; init; }

    [JsonPropertyName("driveRedirection")]
    public bool? DriveRedirection { get; init; }
}
