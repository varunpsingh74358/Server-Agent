using System.Net.WebSockets;
using System.Text;
using CloudOrc.Agent.Contracts.Protocol;

namespace CloudOrc.AgentTestServer;

/// <summary>
/// Handles one agent WebSocket connection end-to-end: registers it with
/// <see cref="AgentSession"/>, reads frames until the connection ends, and prints every
/// recognized message type. Unrecognized/malformed messages are logged, never crash the
/// test server.
/// </summary>
public static class AgentConnectionHandler
{
    public static async Task RunAsync(WebSocket socket, AgentSession session, CancellationToken cancellationToken)
    {
        session.Attach(socket);
        Console.WriteLine("[test-server] Agent connected.");

        var buffer = new byte[8192];

        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                using var messageStream = new MemoryStream();
                WebSocketReceiveResult receiveResult;

                do
                {
                    receiveResult = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (receiveResult.MessageType == WebSocketMessageType.Close)
                    {
                        Console.WriteLine("[test-server] Agent closed the connection.");
                        return;
                    }

                    messageStream.Write(buffer, 0, receiveResult.Count);
                }
                while (!receiveResult.EndOfMessage);

                var json = Encoding.UTF8.GetString(messageStream.ToArray());
                PrintMessage(json);
            }
        }
        catch (WebSocketException ex)
        {
            Console.WriteLine($"[test-server] Connection ended: {ex.Message}");
        }
        finally
        {
            session.Detach(socket);
            Console.WriteLine("[test-server] Agent disconnected.");
        }
    }

    private static void PrintMessage(string json)
    {
        var type = ProtocolJson.TryReadMessageType(json);

        switch (type)
        {
            case ProtocolMessageTypes.Hello:
                Console.WriteLine($"[HELLO] {json}");
                break;
            case ProtocolMessageTypes.Heartbeat:
                Console.WriteLine($"[HEARTBEAT] {json}");
                break;
            case ProtocolMessageTypes.Telemetry:
                Console.WriteLine($"[TELEMETRY] {json}");
                break;
            case ProtocolMessageTypes.CommandStatus:
                Console.WriteLine($"[COMMAND_STATUS] {json}");
                break;
            case ProtocolMessageTypes.CommandResult:
                Console.WriteLine($"[COMMAND_RESULT] {json}");
                break;
            case ProtocolMessageTypes.Error:
                Console.WriteLine($"[ERROR] {json}");
                break;
            default:
                Console.WriteLine($"[test-server] Received unrecognized message (type={type ?? "(none)"}): {json}");
                break;
        }
    }
}
