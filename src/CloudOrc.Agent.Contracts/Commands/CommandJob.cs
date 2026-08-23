using CloudOrc.Agent.Contracts.Abstractions;

namespace CloudOrc.Agent.Contracts.Commands;

/// <summary>
/// A validated command handed off from an ICommandSource to the internal
/// queue. <see cref="SourceReference"/> is an opaque token the originating source uses
/// to know which underlying resource (file, message, etc.) to acknowledge once the
/// command has been executed - the queue and executor never interpret it.
///
/// <see cref="OriginSource"/> lets the consumer (the processing worker) acknowledge the
/// job back to whichever source actually produced it, now that more than one
/// <see cref="ICommandSource"/> can be active at the same time (e.g. local file + WSS).
/// </summary>
public sealed class CommandJob
{
    public required CommandRequest Request { get; init; }

    public required int EffectiveTimeoutSeconds { get; init; }

    public required string SourceReference { get; init; }

    public required ICommandSource OriginSource { get; init; }
}
