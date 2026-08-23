using System.Collections.Concurrent;
using System.Threading.Channels;
using CloudOrc.Agent.Contracts.Abstractions;
using CloudOrc.Agent.Contracts.Commands;
using CloudOrc.ControlAgent.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudOrc.ControlAgent.Backend;

/// <summary>
/// <see cref="ICommandSource"/> fed by <see cref="BackendConnectionService"/> whenever a
/// COMMAND message arrives over the WebSocket. Validation and duplicate protection happen
/// here, before a job is ever queued - exactly mirroring <c>LocalFileCommandSource</c>, so
/// the shared queue and the PowerShell executor never need to know a command came from
/// the network rather than a file.
///
/// Cross-source duplicate protection relies on the same on-disk signal
/// LocalFileCommandSource already checks (results\/completed\/failed\ for the
/// CommandId), plus a light in-memory set scoped to this source. A command with the same
/// CommandId arriving via both the local file source and this one within the same short
/// window - before either has produced a result - could theoretically both be accepted;
/// this is an inherent limitation of combining two independently-operating sources
/// without a single centralized synchronous claim step, and should not occur in practice
/// with correctly-generated unique CommandIds.
/// </summary>
public sealed class WssCommandSource(
    IOptions<ControlAgentOptions> options,
    ILogger<WssCommandSource> logger) : ICommandSource
{
    private readonly ControlAgentOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, byte> _claimedThisSession = new(StringComparer.OrdinalIgnoreCase);

    private readonly Channel<CommandJob> _channel = Channel.CreateUnbounded<CommandJob>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    /// <summary>
    /// Called by <see cref="BackendConnectionService"/>'s receive loop when a COMMAND
    /// message arrives. Returns false (with a reason) if the command was rejected -
    /// the caller is responsible for deciding whether/how to report that back.
    /// </summary>
    public bool TryAcceptIncomingCommand(CommandRequest request, out string? rejectionReason)
    {
        var validation = CommandRequestValidator.Validate(request, _options.Validation);
        if (!validation.IsValid)
        {
            rejectionReason = validation.Error;
            return false;
        }

        var commandId = request.CommandId;

        if (!_claimedThisSession.TryAdd(commandId, 0))
        {
            rejectionReason = $"CommandId '{commandId}' was already received over this connection.";
            return false;
        }

        if (IsAlreadyKnownOnDisk(commandId))
        {
            rejectionReason = $"CommandId '{commandId}' already has a completed/failed result on this agent.";
            return false;
        }

        var job = new CommandJob
        {
            Request = request,
            EffectiveTimeoutSeconds = validation.EffectiveTimeoutSeconds,
            SourceReference = $"wss:{commandId}",
            OriginSource = this
        };

        if (!_channel.Writer.TryWrite(job))
        {
            rejectionReason = "Internal queue rejected the command unexpectedly.";
            return false;
        }

        logger.LogInformation("Accepted command {CommandId} received over the backend connection.", commandId);
        rejectionReason = null;
        return true;
    }

    public IAsyncEnumerable<CommandJob> GetCommandsAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);

    public Task AcknowledgeAsync(CommandJob job, bool succeeded, CancellationToken cancellationToken)
    {
        // There is no file to move for a WSS-origin command. The terminal outcome is
        // already communicated to the backend by WssResultSink; this hook exists only to
        // satisfy the ICommandSource contract symmetrically with LocalFileCommandSource.
        logger.LogDebug("WSS-origin command {CommandId} acknowledged (succeeded={Succeeded}).", job.Request.CommandId, succeeded);
        return Task.CompletedTask;
    }

    private bool IsAlreadyKnownOnDisk(string commandId)
    {
        var resultPath = Path.Combine(_options.ResultsDirectory, $"{commandId}.result.json");
        var completedPath = Path.Combine(_options.CompletedDirectory, $"{commandId}.json");
        var failedPath = Path.Combine(_options.FailedDirectory, $"{commandId}.json");

        return File.Exists(resultPath) || File.Exists(completedPath) || File.Exists(failedPath);
    }
}
