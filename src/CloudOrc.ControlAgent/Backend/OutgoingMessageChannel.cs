using System.Threading.Channels;

namespace CloudOrc.ControlAgent.Backend;

/// <summary>
/// Single-consumer queue of already-serialized JSON messages waiting to go out over the
/// backend WebSocket. Every publisher (heartbeat, telemetry, command status, command
/// result) writes here instead of touching the socket directly - <see cref="BackendConnectionService"/>
/// is the only thing that ever calls <c>ClientWebSocket.SendAsync</c>, which is what makes
/// concurrent sends from multiple background services safe.
///
/// Unbounded and not cleared on reconnect: a message written while disconnected simply
/// waits here until the next successful connection's send loop starts draining it again.
/// Every message carries its own generation timestamp, so a consumer that receives a
/// delayed HEARTBEAT/TELEMETRY after a long outage can tell it is stale. This is a
/// deliberate simplification for the local-testing phase - see docs/ARCHITECTURE.md for
/// the honest delivery-guarantee discussion.
/// </summary>
public sealed class OutgoingMessageChannel
{
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    public bool TryEnqueue(string json) => _channel.Writer.TryWrite(json);

    public IAsyncEnumerable<string> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
