using System.Text.Json.Serialization;
using CloudOrc.Agent.Contracts.Commands;

namespace CloudOrc.Agent.Contracts.Protocol;

/// <summary>Backend -> Agent: a command to execute, wrapping the same CommandRequest shape used by the local file source.</summary>
public sealed class CommandMessage
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = ProtocolMessageTypes.Command;

    [JsonPropertyName("command")]
    public required CommandRequest Command { get; init; }
}
