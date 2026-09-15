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

    // The following three properties are the desired-state payload for
    // CommandTypes.RdpTransferPolicyApply only; they are unused (and should be left null)
    // for CommandTypes.PowerShellExec. Kept as plain fields on the shared request rather
    // than a nested object so LocalFileCommandSource's generic CommandRequest deserialization
    // keeps working unchanged for every commandType.

    /// <summary>Desired client-to-server clipboard redirection mode. Only "PLAIN_TEXT_ONLY" is currently accepted.</summary>
    [JsonPropertyName("clientToServerClipboard")]
    public string? ClientToServerClipboard { get; init; }

    /// <summary>Desired server-to-client clipboard redirection mode. Only "PLAIN_TEXT_ONLY" is currently accepted.</summary>
    [JsonPropertyName("serverToClientClipboard")]
    public string? ServerToClientClipboard { get; init; }

    /// <summary>Whether client drive redirection should be allowed. Only "false" (blocked) is currently accepted.</summary>
    [JsonPropertyName("driveRedirection")]
    public bool? DriveRedirection { get; init; }
}
