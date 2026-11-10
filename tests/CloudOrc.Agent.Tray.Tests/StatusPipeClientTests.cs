using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using CloudOrc.Agent.Tray;
using CloudOrc.Agent.Tray.Protocol;

namespace CloudOrc.Agent.Tray.Tests;

public sealed class StatusPipeClientTests
{
    /// <summary>In-memory duplex stream: reads come from a canned response, writes are captured.</summary>
    private sealed class FakeDuplexStream(string responseText) : Stream
    {
        private readonly MemoryStream _readSource = new(Encoding.UTF8.GetBytes(responseText));

        public MemoryStream Written { get; } = new();

        public string WrittenText => Encoding.UTF8.GetString(Written.ToArray());

        public bool Disposed { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _readSource.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Written.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    /// <summary>Stream whose reads never complete until cancelled - simulates a hung service.</summary>
    private sealed class HangingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { }
    }

    private const string ConnectedJson =
        "{\"ok\":true,\"state\":\"CONNECTED\",\"deviceName\":\"SRV-01\",\"agentVersion\":\"1.2.3\"," +
        "\"lastContactAt\":\"2026-11-09T10:00:00+00:00\",\"canReconnect\":false,\"diagnosticsId\":\"diag-1\"," +
        "\"protectionSummary\":\"Protected\",\"portalUrl\":\"https://portal.example.com\"}\n";

    [Fact]
    public async Task SendAsync_WritesOneJsonLineAndParsesOneResponseLine()
    {
        var stream = new FakeDuplexStream(ConnectedJson);
        var client = new StatusPipeClient(_ => Task.FromResult<Stream>(stream));

        var result = await client.GetStatusAsync(CancellationToken.None);

        Assert.True(result.IsAvailable);
        Assert.Equal(LocalAgentStates.Connected, result.Response!.State);
        Assert.Equal("SRV-01", result.Response.DeviceName);
        Assert.Equal("https://portal.example.com", result.Response.PortalUrl);
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero), result.Response.LastContactAt);

        var written = stream.WrittenText;
        Assert.EndsWith("\n", written);
        Assert.Single(written.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        using var doc = JsonDocument.Parse(written);
        Assert.Equal("status", doc.RootElement.GetProperty("action").GetString());
        Assert.False(doc.RootElement.TryGetProperty("code", out _)); // null code omitted
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task SendAsync_ActivateCarriesCodeAndSurfacesServerError()
    {
        var stream = new FakeDuplexStream("{\"ok\":false,\"error\":\"Code expired\",\"state\":\"NOT_ACTIVATED\",\"deviceName\":\"SRV-01\",\"agentVersion\":\"1.2.3\",\"diagnosticsId\":\"d\"}\n");
        var client = new StatusPipeClient(_ => Task.FromResult<Stream>(stream));

        var result = await client.SendAsync(
            new LocalStatusRequest { Action = LocalStatusProtocol.ActionActivate, Code = "ABCD-1234" },
            CancellationToken.None);

        using var doc = JsonDocument.Parse(stream.WrittenText);
        Assert.Equal("activate", doc.RootElement.GetProperty("action").GetString());
        Assert.Equal("ABCD-1234", doc.RootElement.GetProperty("code").GetString());
        Assert.False(result.Response!.Ok);
        Assert.Equal("Code expired", result.Response.Error);
    }

    [Fact]
    public async Task SendAsync_ConnectTimeout_IsServiceUnavailable()
    {
        var client = new StatusPipeClient(_ => throw new TimeoutException());

        var result = await client.GetStatusAsync(CancellationToken.None);

        Assert.False(result.IsAvailable);
        Assert.Equal(TrayViewModel.ServiceNotRunningText, result.UnavailableReason);
    }

    [Fact]
    public async Task SendAsync_IOException_IsServiceUnavailable()
    {
        var client = new StatusPipeClient(_ => Task.FromException<Stream>(new IOException("pipe broken")));

        var result = await client.GetStatusAsync(CancellationToken.None);

        Assert.False(result.IsAvailable);
        Assert.Equal(TrayViewModel.ServiceNotRunningText, result.UnavailableReason);
    }

    [Fact]
    public async Task SendAsync_EmptyResponse_IsUnavailable()
    {
        var client = new StatusPipeClient(_ => Task.FromResult<Stream>(new FakeDuplexStream(string.Empty)));

        var result = await client.GetStatusAsync(CancellationToken.None);

        Assert.False(result.IsAvailable);
        Assert.NotNull(result.UnavailableReason);
    }

    [Fact]
    public async Task SendAsync_MalformedJson_IsUnavailable()
    {
        var client = new StatusPipeClient(_ => Task.FromResult<Stream>(new FakeDuplexStream("{not json\n")));

        var result = await client.GetStatusAsync(CancellationToken.None);

        Assert.False(result.IsAvailable);
        Assert.Contains("unreadable", result.UnavailableReason);
    }

    [Fact]
    public async Task SendAsync_CallerCancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var client = new StatusPipeClient(ct => Task.FromCanceled<Stream>(ct));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetStatusAsync(cts.Token));
    }

    [Fact]
    public async Task SendAsync_HungServiceWithinCallerTimeout_PropagatesCallerCancellation()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var client = new StatusPipeClient(_ => Task.FromResult<Stream>(new HangingStream()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetStatusAsync(cts.Token));
    }

    [Fact]
    public async Task CreateForPipe_NoServer_IsServiceUnavailable()
    {
        var client = StatusPipeClient.CreateForPipe($"CloudOrc.Agent.Status.Test.{Guid.NewGuid():N}");

        var result = await client.GetStatusAsync(CancellationToken.None);

        Assert.False(result.IsAvailable);
        Assert.Equal(TrayViewModel.ServiceNotRunningText, result.UnavailableReason);
    }

    [Fact]
    public async Task CreateForPipe_RealNamedPipeRoundTrip()
    {
        var pipeName = $"CloudOrc.Agent.Status.Test.{Guid.NewGuid():N}";
        string? receivedRequest = null;

        var serverTask = Task.Run(async () =>
        {
            await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await server.WaitForConnectionAsync();
            using var reader = new StreamReader(server, Encoding.UTF8, false, 4096, leaveOpen: true);
            receivedRequest = await reader.ReadLineAsync();
            var bytes = Encoding.UTF8.GetBytes(ConnectedJson);
            await server.WriteAsync(bytes);
            await server.FlushAsync();
            server.WaitForPipeDrain();
        });

        var client = StatusPipeClient.CreateForPipe(pipeName);
        var result = await client.SendAsync(new LocalStatusRequest { Action = LocalStatusProtocol.ActionRetry }, CancellationToken.None);
        await serverTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(result.IsAvailable);
        Assert.Equal(LocalAgentStates.Connected, result.Response!.State);
        Assert.NotNull(receivedRequest);
        using var doc = JsonDocument.Parse(receivedRequest!);
        Assert.Equal("retry", doc.RootElement.GetProperty("action").GetString());
    }
}
