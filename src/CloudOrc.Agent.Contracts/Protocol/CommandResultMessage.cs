using System.Text.Json.Serialization;
using CloudOrc.Agent.Contracts.Commands;

namespace CloudOrc.Agent.Contracts.Protocol;

/// <summary>
/// The wire form of a terminal <see cref="CommandResult"/>. Kept as its own DTO (rather
/// than reusing CommandResult directly) so the wire shape can evolve independently of the
/// internal result model, even though today the fields match one-to-one.
/// </summary>
public sealed class CommandResultMessage
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = ProtocolMessageTypes.CommandResult;

    [JsonPropertyName("commandId")]
    public required string CommandId { get; init; }

    [JsonPropertyName("status")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required CommandStatus Status { get; init; }

    [JsonPropertyName("startedAt")]
    public required DateTimeOffset StartedAt { get; init; }

    [JsonPropertyName("completedAt")]
    public required DateTimeOffset CompletedAt { get; init; }

    [JsonPropertyName("durationMilliseconds")]
    public required long DurationMilliseconds { get; init; }

    [JsonPropertyName("output")]
    public IReadOnlyList<string> Output { get; init; } = [];

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    public static CommandResultMessage FromCommandResult(CommandResult result) => new()
    {
        CommandId = result.CommandId,
        Status = result.Status,
        StartedAt = result.StartedAt,
        CompletedAt = result.CompletedAt,
        DurationMilliseconds = result.DurationMilliseconds,
        Output = result.Output,
        Error = result.Error
    };
}
