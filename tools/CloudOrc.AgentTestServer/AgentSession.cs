using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CloudOrc.Agent.Contracts.Protocol;

namespace CloudOrc.AgentTestServer;

/// <summary>
/// Tracks the single currently-connected agent (this is a simple one-at-a-time test
/// harness, not a multi-agent management server) and provides the one place that sends
/// frames to it, so console-driven sends and any future concurrent sends stay safe.
/// </summary>
public sealed class AgentSession
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private WebSocket? _socket;

    public bool IsConnected => _socket is { State: WebSocketState.Open };

    public void Attach(WebSocket socket)
    {
        if (IsConnected)
        {
            Console.WriteLine("[test-server] A new agent connection replaced the previous one.");
        }

        _socket = socket;
    }

    public void Detach(WebSocket socket)
    {
        if (ReferenceEquals(_socket, socket))
        {
            _socket = null;
        }
    }

    public async Task<bool> SendCommandAsync(string commandId, string script, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var message = new CommandMessage
        {
            CommandId = commandId,
            CorrelationId = $"corr-{Guid.NewGuid():N}"[..17],
            CommandType = "powershell-exec",
            CreatedAt = DateTimeOffset.UtcNow,
            Parameters = new CommandParameters
            {
                Script = script,
                TimeoutSeconds = timeoutSeconds
            }
        };

        return await SendAsync(message, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> SendPingAsync(CancellationToken cancellationToken) =>
        await SendAsync(new PingMessage(), cancellationToken).ConfigureAwait(false);

    private async Task<bool> SendAsync<T>(T message, CancellationToken cancellationToken)
    {
        var socket = _socket;
        if (socket is not { State: WebSocketState.Open })
        {
            Console.WriteLine("[test-server] No agent is currently connected.");
            return false;
        }

        var json = JsonSerializer.Serialize(message, ProtocolJson.Options);
        var bytes = Encoding.UTF8.GetBytes(json);

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _sendLock.Release();
        }
    }
}
