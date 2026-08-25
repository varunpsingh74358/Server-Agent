using System.Collections.Concurrent;
using System.Threading.Channels;

namespace CloudOrc.ControlAgent.Backend;

/// <summary>
/// Fan-out hub of already-serialized JSON messages waiting to go out over one or more
/// backend WebSocket connections. Every publisher (heartbeat, telemetry, command status,
/// command result) writes here instead of touching a socket directly -
/// <see cref="BackendConnectionService"/> is the only thing that ever calls
/// <c>ClientWebSocket.SendAsync</c>, which is what makes concurrent sends from multiple
/// background services safe.
///
/// Holds one independent queue per named backend target. <see cref="TryEnqueue"/>
/// broadcasts to every registered target (what a heartbeat/telemetry/result message
/// wants: every connected backend should see it). <see cref="TryEnqueueTo"/> sends to one
/// specific target only, for replies that must go back on the same connection that
/// triggered them (a PING reply, a rejected-command error).
///
/// A bare <c>new OutgoingMessageChannel()</c> auto-registers a single "default" target, so
/// every existing single-backend caller/test keeps working with no setup: TryEnqueue and
/// the parameterless ReadAllAsync behave exactly as before, with exactly one recipient.
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
    public const string DefaultTargetName = "default";

    private readonly ConcurrentDictionary<string, Channel<string>> _targets = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="registerDefaultTarget">
    /// True (the default) auto-registers the "default" target so a bare
    /// <c>new OutgoingMessageChannel()</c> immediately supports the legacy single-backend
    /// TryEnqueue/ReadAllAsync(ct) calls with no setup. Pass false only when the caller is
    /// about to register the exact set of backend targets itself (see Program.cs), so an
    /// unused, never-drained "default" queue doesn't sit around accumulating messages
    /// forever when no connection actually uses that name.
    /// </param>
    public OutgoingMessageChannel(bool registerDefaultTarget = true)
    {
        if (registerDefaultTarget)
        {
            RegisterTarget(DefaultTargetName);
        }
    }

    public void RegisterTarget(string targetName) =>
        _targets.GetOrAdd(targetName, static _ => Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        }));

    /// <summary>Broadcasts to every registered target. Returns true if at least one target accepted the message.</summary>
    public bool TryEnqueue(string json)
    {
        var delivered = false;
        foreach (var channel in _targets.Values)
        {
            if (channel.Writer.TryWrite(json))
            {
                delivered = true;
            }
        }

        return delivered;
    }

    /// <summary>Sends to one specific target only. Returns false if that target isn't registered.</summary>
    public bool TryEnqueueTo(string targetName, string json) =>
        _targets.TryGetValue(targetName, out var channel) && channel.Writer.TryWrite(json);

    public IAsyncEnumerable<string> ReadAllAsync(CancellationToken cancellationToken) =>
        ReadAllAsync(DefaultTargetName, cancellationToken);

    public IAsyncEnumerable<string> ReadAllAsync(string targetName, CancellationToken cancellationToken) =>
        _targets[targetName].Reader.ReadAllAsync(cancellationToken);
}
