using CloudOrc.Agent.Contracts.Abstractions;
using CloudOrc.Agent.Contracts.Commands;
using CloudOrc.Agent.Contracts.Execution;
using CloudOrc.ControlAgent.Health;
using CloudOrc.ControlAgent.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudOrc.ControlAgent.Tests.Services;

/// <summary>
/// Exercises the actual sequential pipeline in <see cref="CommandProcessingService"/> -
/// queue drain, executor invocation, result sink fan-out, status publishing, and health
/// touch - using fakes for everything the pipeline is transport-blind to. There was no
/// prior direct test coverage of this class.
/// </summary>
public sealed class CommandProcessingServiceTests
{
    private static CommandJob CreateJob(string commandId, string? correlationId, RecordingCommandSource origin) => new()
    {
        Request = new CommandRequest { CommandId = commandId, CorrelationId = correlationId, Script = "irrelevant-for-these-tests", TimeoutSeconds = 30 },
        EffectiveTimeoutSeconds = 30,
        SourceReference = $"test:{commandId}",
        OriginSource = origin
    };

    [Fact]
    public async Task ProcessOneAsync_SuccessfulCommand_WritesResultToEverySink_AndAcknowledgesOrigin()
    {
        var queue = new InMemoryCommandQueue();
        var sinkA = new RecordingResultSink();
        var sinkB = new RecordingResultSink();
        var statusPublisher = new RecordingStatusPublisher();
        var executor = new ScriptedExecutor(_ => Task.FromResult(SuccessOutcome(["ok"])));
        var origin = new RecordingCommandSource();
        var health = new ControlAgentHealthState();

        var service = new CommandProcessingService(queue, [sinkA, sinkB], statusPublisher, executor, health, NullLogger<CommandProcessingService>.Instance);

        await queue.EnqueueAsync(CreateJob("cmd-1", "corr-1", origin), CancellationToken.None);

        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => sinkA.Results.Count >= 1 && sinkB.Results.Count >= 1);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal("cmd-1", sinkA.Results[0].CommandId);
        Assert.Equal("corr-1", sinkA.Results[0].CorrelationId);
        Assert.Equal(CommandStatus.Success, sinkA.Results[0].Status);
        Assert.Equal("cmd-1", sinkB.Results[0].CommandId);

        Assert.Single(origin.Acknowledgements);
        Assert.True(origin.Acknowledgements[0].Succeeded);

        Assert.Contains(statusPublisher.Published, p => p.CommandId == "cmd-1" && p.CorrelationId == "corr-1" && p.Status == CommandStatus.Running);
    }

    [Fact]
    public async Task ProcessQueueAsync_MultipleCommands_AreExecutedStrictlySequentially()
    {
        var queue = new InMemoryCommandQueue();
        var sink = new RecordingResultSink();
        var statusPublisher = new RecordingStatusPublisher();
        var timeline = new TimelineRecordingExecutor(TimeSpan.FromMilliseconds(150));
        var origin = new RecordingCommandSource();
        var health = new ControlAgentHealthState();

        var service = new CommandProcessingService(queue, [sink], statusPublisher, timeline, health, NullLogger<CommandProcessingService>.Instance);

        await queue.EnqueueAsync(CreateJob("cmd-a", null, origin), CancellationToken.None);
        await queue.EnqueueAsync(CreateJob("cmd-b", null, origin), CancellationToken.None);
        await queue.EnqueueAsync(CreateJob("cmd-c", null, origin), CancellationToken.None);

        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => sink.Results.Count >= 3, TimeSpan.FromSeconds(10));
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(3, timeline.Intervals.Count);
        var ordered = timeline.Intervals.OrderBy(i => i.Start).ToList();
        for (var i = 1; i < ordered.Count; i++)
        {
            Assert.True(
                ordered[i].Start >= ordered[i - 1].End,
                $"Command starting at {ordered[i].Start:O} overlapped with one that ended at {ordered[i - 1].End:O} - execution should never run concurrently.");
        }
    }

    [Fact]
    public async Task ProcessOneAsync_LongRunningCommand_HeartbeatLoopKeepsTouchingHealth()
    {
        // CommandProcessingService's own heartbeat loop (health.TouchProcessing() every 2s)
        // runs concurrently with ProcessOneAsync via Task.WhenAll - it must never be
        // blocked by a slow command. This is the same architectural independence that lets
        // TelemetryPublisherService/HeartbeatPublisherService (separate BackgroundServices
        // reading the same health snapshot) keep publishing while a command executes.
        var queue = new InMemoryCommandQueue();
        var sink = new RecordingResultSink();
        var statusPublisher = new RecordingStatusPublisher();
        var executor = new ScriptedExecutor(async _ =>
        {
            await Task.Delay(TimeSpan.FromSeconds(4.5));
            return SuccessOutcome(["done"]);
        });
        var origin = new RecordingCommandSource();
        var health = new ControlAgentHealthState();

        var service = new CommandProcessingService(queue, [sink], statusPublisher, executor, health, NullLogger<CommandProcessingService>.Instance);

        await queue.EnqueueAsync(CreateJob("cmd-long", null, origin), CancellationToken.None);
        await service.StartAsync(CancellationToken.None);

        var timestampsWhileRunning = new List<DateTimeOffset>();
        while (sink.Results.Count == 0)
        {
            var snapshot = health.Snapshot(TimeSpan.FromSeconds(30));
            if (snapshot.CurrentCommandStatus == CommandStatus.Running)
            {
                timestampsWhileRunning.Add(snapshot.LastProcessingActivityAt);
            }

            await Task.Delay(200);
        }

        await service.StopAsync(CancellationToken.None);

        var distinctTimestamps = timestampsWhileRunning.Distinct().Count();
        Assert.True(distinctTimestamps >= 2, $"Expected the heartbeat loop to advance LastProcessingActivityAt at least twice while the 3s command ran; observed {distinctTimestamps} distinct value(s).");
    }

    private static PowerShellExecutionOutcome SuccessOutcome(IReadOnlyList<string> output) => new()
    {
        Status = CommandStatus.Success,
        StartedAt = DateTimeOffset.UtcNow,
        CompletedAt = DateTimeOffset.UtcNow,
        DurationMilliseconds = 0,
        Output = output,
        Error = null
    };

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.True(condition(), "Condition was not met within the expected time.");
    }

    private sealed class RecordingResultSink : ICommandResultSink
    {
        public List<CommandResult> Results { get; } = [];

        public Task WriteAsync(CommandResult result, CancellationToken cancellationToken)
        {
            Results.Add(result);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingStatusPublisher : ICommandStatusPublisher
    {
        public List<(string CommandId, string? CorrelationId, CommandStatus Status)> Published { get; } = [];

        public Task PublishStatusAsync(string commandId, string? correlationId, CommandStatus status, CancellationToken cancellationToken)
        {
            Published.Add((commandId, correlationId, status));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingCommandSource : ICommandSource
    {
        public List<(CommandJob Job, bool Succeeded)> Acknowledgements { get; } = [];

        public IAsyncEnumerable<CommandJob> GetCommandsAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by these tests - jobs are enqueued directly.");

        public Task AcknowledgeAsync(CommandJob job, bool succeeded, CancellationToken cancellationToken)
        {
            Acknowledgements.Add((job, succeeded));
            return Task.CompletedTask;
        }
    }

    private sealed class ScriptedExecutor(Func<string, Task<PowerShellExecutionOutcome>> handler) : IPowerShellExecutor
    {
        public Task<PowerShellExecutionOutcome> ExecuteAsync(string script, TimeSpan timeout, CancellationToken cancellationToken) =>
            handler(script);
    }

    private sealed class TimelineRecordingExecutor(TimeSpan delayPerCommand) : IPowerShellExecutor
    {
        private readonly Lock _sync = new();

        public List<(DateTimeOffset Start, DateTimeOffset End)> Intervals { get; } = [];

        public async Task<PowerShellExecutionOutcome> ExecuteAsync(string script, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var start = DateTimeOffset.UtcNow;
            await Task.Delay(delayPerCommand, cancellationToken);
            var end = DateTimeOffset.UtcNow;

            lock (_sync)
            {
                Intervals.Add((start, end));
            }

            return SuccessOutcome(["ok"]);
        }
    }
}
