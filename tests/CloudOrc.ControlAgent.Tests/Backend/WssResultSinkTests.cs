using CloudOrc.Agent.Contracts.Commands;
using CloudOrc.ControlAgent.Backend;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudOrc.ControlAgent.Tests.Backend;

public class WssResultSinkTests
{
    [Fact]
    public async Task WriteAsync_NoConnectionEverEstablished_DoesNotThrow()
    {
        // Simulates the "backend connection is temporarily unavailable" requirement:
        // nothing is reading from the channel (as would be the case while disconnected),
        // but writing a result must still succeed without throwing.
        var outgoing = new OutgoingMessageChannel();
        var sink = new WssResultSink(outgoing, NullLogger<WssResultSink>.Instance);
        var result = new CommandResult
        {
            CommandId = "test-001",
            Status = CommandStatus.Success,
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            DurationMilliseconds = 10
        };

        var exception = await Record.ExceptionAsync(() => sink.WriteAsync(result, CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact]
    public async Task WriteAsync_EnqueuesAResultMessageContainingTheCommandId()
    {
        var outgoing = new OutgoingMessageChannel();
        var sink = new WssResultSink(outgoing, NullLogger<WssResultSink>.Instance);
        var result = new CommandResult
        {
            CommandId = "test-002",
            Status = CommandStatus.Failed,
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            DurationMilliseconds = 5,
            Error = "boom"
        };

        await sink.WriteAsync(result, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var json in outgoing.ReadAllAsync(cts.Token))
        {
            Assert.Contains("test-002", json);
            Assert.Contains("COMMAND_RESULT", json);
            return;
        }

        Assert.Fail("Expected the result message to be enqueued.");
    }
}
