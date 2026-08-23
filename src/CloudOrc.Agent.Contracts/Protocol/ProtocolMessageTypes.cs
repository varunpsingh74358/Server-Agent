namespace CloudOrc.Agent.Contracts.Protocol;

/// <summary>
/// The "type" discriminator value carried by every protocol message. Every message the
/// agent sends or receives over the backend WebSocket connection has one of these -
/// there is no unstructured/untyped JSON traffic.
/// </summary>
public static class ProtocolMessageTypes
{
    // Agent -> Backend
    public const string Hello = "HELLO";
    public const string Heartbeat = "HEARTBEAT";
    public const string Telemetry = "TELEMETRY";
    public const string CommandStatus = "COMMAND_STATUS";
    public const string CommandResult = "COMMAND_RESULT";
    public const string Error = "ERROR";

    // Backend -> Agent
    public const string Command = "COMMAND";
    public const string Ping = "PING";
}
