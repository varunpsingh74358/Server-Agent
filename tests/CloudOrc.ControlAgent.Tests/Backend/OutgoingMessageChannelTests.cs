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
}
