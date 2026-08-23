using System.Threading.Channels;
using CloudOrc.Agent.Contracts.Abstractions;
using CloudOrc.Agent.Contracts.Commands;

namespace CloudOrc.ControlAgent.Services;

/// <summary>
/// Channel-backed implementation of <see cref="ICommandQueue"/>. Unbounded, single
/// logical consumer - the point is to decouple file detection from PowerShell execution,
/// not to provide durability. If the process restarts, anything sitting in this queue is
/// lost; the file-based command source is responsible for re-discovering unprocessed work.
/// </summary>
public sealed class InMemoryCommandQueue : ICommandQueue
{
    private readonly Channel<CommandJob> _channel = Channel.CreateUnbounded<CommandJob>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = true
    });

    public ValueTask EnqueueAsync(CommandJob job, CancellationToken cancellationToken) =>
        _channel.Writer.WriteAsync(job, cancellationToken);

    public IAsyncEnumerable<CommandJob> DequeueAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
