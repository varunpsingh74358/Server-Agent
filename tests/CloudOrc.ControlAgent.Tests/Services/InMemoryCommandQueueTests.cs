using CloudOrc.Agent.Contracts.Abstractions;
using CloudOrc.Agent.Contracts.Commands;
using CloudOrc.ControlAgent.Services;

namespace CloudOrc.ControlAgent.Tests.Services;

public class InMemoryCommandQueueTests
{
    private sealed class FakeCommandSource : ICommandSource
    {
        public IAsyncEnumerable<CommandJob> GetCommandsAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by these tests.");

        public Task AcknowledgeAsync(CommandJob job, bool succeeded, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private static readonly ICommandSource TestSource = new FakeCommandSource();

    private static CommandJob MakeJob(string id) => new()
    {
        Request = new CommandRequest { CommandId = id, Script = "Get-Date" },
        EffectiveTimeoutSeconds = 30,
        SourceReference = $@"C:\processing\{id}.json",
        OriginSource = TestSource
    };

    [Fact]
    public async Task EnqueueThenDequeue_ReturnsSameJob()
    {
        var queue = new InMemoryCommandQueue();
        var job = MakeJob("test-001");

        await queue.EnqueueAsync(job, CancellationToken.None);

        using var cts = new CancellationTokenSource();
        await foreach (var dequeued in queue.DequeueAllAsync(cts.Token))
        {
            Assert.Equal(job.Request.CommandId, dequeued.Request.CommandId);
            return;
        }

        Assert.Fail("Expected to dequeue exactly one job.");
    }

    [Fact]
    public async Task Dequeue_PreservesFifoOrder()
    {
        var queue = new InMemoryCommandQueue();
        await queue.EnqueueAsync(MakeJob("first"), CancellationToken.None);
        await queue.EnqueueAsync(MakeJob("second"), CancellationToken.None);
        await queue.EnqueueAsync(MakeJob("third"), CancellationToken.None);

        var results = new List<string>();
        using var cts = new CancellationTokenSource();
        await foreach (var job in queue.DequeueAllAsync(cts.Token))
        {
            results.Add(job.Request.CommandId);
            if (results.Count == 3)
            {
                break;
            }
        }

        Assert.Equal(["first", "second", "third"], results);
    }

    [Fact]
    public async Task Dequeue_RespectsCancellation()
    {
        var queue = new InMemoryCommandQueue();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in queue.DequeueAllAsync(cts.Token))
            {
            }
        });
    }
}
