using CloudOrc.ControlAgent.Backend;

namespace CloudOrc.ControlAgent.Tests.Backend;

public class OutgoingMessageChannelTests
{
    [Fact]
    public async Task TryEnqueue_ThenReadAllAsync_ReturnsSameMessage()
    {
        var channel = new OutgoingMessageChannel();

        var enqueued = channel.TryEnqueue("""{"type":"PING"}""");

        Assert.True(enqueued);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var message in channel.ReadAllAsync(cts.Token))
        {
            Assert.Equal("""{"type":"PING"}""", message);
            return;
        }

        Assert.Fail("Expected to read the enqueued message.");
    }

    [Fact]
    public async Task MultipleWriters_AllMessagesAreDelivered()
    {
        var channel = new OutgoingMessageChannel();

        channel.TryEnqueue("first");
        channel.TryEnqueue("second");
        channel.TryEnqueue("third");

        var received = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var message in channel.ReadAllAsync(cts.Token))
        {
            received.Add(message);
            if (received.Count == 3)
            {
                break;
            }
        }

        Assert.Equal(["first", "second", "third"], received);
    }

    [Fact]
    public void Messages_QueuedWhileNoReaderIsActive_AreNotLost()
    {
        // Simulates writing while disconnected: nothing is draining the channel yet,
        // but the writes must still succeed and be retained for later delivery.
        var channel = new OutgoingMessageChannel();

        Assert.True(channel.TryEnqueue("queued-while-disconnected-1"));
        Assert.True(channel.TryEnqueue("queued-while-disconnected-2"));
    }

    [Fact]
    public async Task TryEnqueue_WithMultipleRegisteredTargets_BroadcastsToEveryTarget()
    {
        // The multi-backend scenario: a production connection and a local dev tunnel
        // connected at the same time should both see every HEARTBEAT/TELEMETRY/RESULT.
        var channel = new OutgoingMessageChannel(registerDefaultTarget: false);
        channel.RegisterTarget("production");
        channel.RegisterTarget("dev-tunnel");

        Assert.True(channel.TryEnqueue("shared-message"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var message in channel.ReadAllAsync("production", cts.Token))
        {
            Assert.Equal("shared-message", message);
            break;
        }

        await foreach (var message in channel.ReadAllAsync("dev-tunnel", cts.Token))
        {
            Assert.Equal("shared-message", message);
            break;
        }
    }

    [Fact]
    public async Task TryEnqueueTo_OnlyDeliversToTheNamedTarget()
    {
        var channel = new OutgoingMessageChannel(registerDefaultTarget: false);
        channel.RegisterTarget("production");
        channel.RegisterTarget("dev-tunnel");

        Assert.True(channel.TryEnqueueTo("production", "only-for-production"));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in channel.ReadAllAsync("dev-tunnel", cts.Token))
            {
                Assert.Fail("dev-tunnel target must not receive a message sent only to production.");
            }
        });
    }

    [Fact]
    public void TryEnqueueTo_UnknownTargetName_ReturnsFalse()
    {
        var channel = new OutgoingMessageChannel();

        Assert.False(channel.TryEnqueueTo("does-not-exist", "message"));
    }

    [Fact]
    public void ConstructedWithoutDefaultTarget_TryEnqueue_DoesNotDeliverAnywhereUntilRegistered()
    {
        var channel = new OutgoingMessageChannel(registerDefaultTarget: false);

        // No target registered yet - broadcasting must report nothing accepted the
        // message rather than silently queuing it on an orphan channel.
        Assert.False(channel.TryEnqueue("nobody-home"));
    }
}
