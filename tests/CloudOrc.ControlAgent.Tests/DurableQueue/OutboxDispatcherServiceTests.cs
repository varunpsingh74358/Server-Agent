using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using CloudOrc.Agent.Contracts.Identity;
using CloudOrc.ControlAgent.Configuration;
using CloudOrc.ControlAgent.DurableQueue;
using CloudOrc.ControlAgent.Enrollment;
using CloudOrc.ControlAgent.Transport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudOrc.ControlAgent.Tests.DurableQueue;

/// <summary>End-to-end delivery against a real loopback HTTP listener.</summary>
public sealed class OutboxDispatcherServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cloudorc-outbox-" + Guid.NewGuid().ToString("N"));
    private readonly AgentStateDatabase _db;
    private readonly OutboxStore _outbox;
    private readonly HttpListener _listener = new();
    private readonly ConcurrentQueue<(string Path, string? Auth)> _received = new();
    private readonly ConcurrentDictionary<string, int> _statusByEvent = new();
    private readonly BackendApiClient _api;
    private readonly OutboxDispatcherService _dispatcher;

    public OutboxDispatcherServiceTests()
    {
        _db = new AgentStateDatabase(Path.Combine(_dir, "state.db"), NullLogger<AgentStateDatabase>.Instance, hardenDirectoryAcl: false);
        _outbox = new OutboxStore(_db);

        var port = FreePort();
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Start();
        _ = Task.Run(ServeAsync);

        var controlOptions = Options.Create(new ControlAgentOptions { DataDirectory = _dir });
        var store = new EnrolledStateStore(controlOptions, NullLogger<EnrolledStateStore>.Instance);
        var identity = new AgentIdentity { AgentId = "a", ServerId = "s", MachineId = "m", MachineName = "n", AgentVersion = "1", Credential = "test-credential" };
        _api = new BackendApiClient(
            Options.Create(new BackupOptions()),
            Options.Create(new BackendConnectionOptions { Url = $"ws://localhost:{port}/agent", DevelopmentAllowInsecureWs = true }),
            store,
            new AgentCredentialProvider(identity, store),
            TimeProvider.System,
            NullLogger<BackendApiClient>.Instance);
        _dispatcher = new OutboxDispatcherService(_outbox, _api, new BackendLinkState(), TimeProvider.System, NullLogger<OutboxDispatcherService>.Instance);
    }

    public void Dispose()
    {
        _listener.Stop();
        _api.Dispose();
        _db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public async Task Success_AndDuplicate409_AreBothTreatedAsDelivered_WithBearerCredential()
    {
        Enqueue("ok-1");
        Enqueue("dup-1");
        _statusByEvent["dup-1"] = 409;

        await _dispatcher.DrainAsync(CancellationToken.None);

        Assert.Empty(_outbox.PeekHead(10));
        Assert.All(_received, r => Assert.Equal("Bearer test-credential", r.Auth));
        Assert.Equal(["/api/v1/backup/jobs/ok-1/events", "/api/v1/backup/jobs/dup-1/events"], _received.Select(r => r.Path));
    }

    [Fact]
    public async Task ServerError_StopsTheBatch_AndSchedulesARetry_PreservingOrder()
    {
        Enqueue("busy-1");
        Enqueue("after-1");
        _statusByEvent["busy-1"] = 503;

        var wait = await _dispatcher.DrainAsync(CancellationToken.None);

        var head = _outbox.PeekHead(10);
        Assert.Equal(["busy-1", "after-1"], head.Select(i => i.EventId));
        Assert.Equal(1, head[0].Attempts);
        Assert.True(wait > TimeSpan.Zero);
        Assert.Single(_received); // "after-1" was not sent ahead of "busy-1"
    }

    [Fact]
    public async Task PermanentRejection_IsParkedAsDead_SoLaterEventsStillFlow()
    {
        Enqueue("poison-1");
        Enqueue("next-1");
        _statusByEvent["poison-1"] = 400;

        await _dispatcher.DrainAsync(CancellationToken.None);

        Assert.Empty(_outbox.PeekHead(10));
        Assert.Equal(2, _received.Count);
    }

    [Fact]
    public async Task TunnelOffline404_IsRetried_NotParkedAsDead()
    {
        Enqueue("tunnel-1");
        _statusByEvent["tunnel-1"] = 404;

        var wait = await _dispatcher.DrainAsync(CancellationToken.None);

        var head = _outbox.PeekHead(10);
        Assert.Equal(["tunnel-1"], head.Select(i => i.EventId));
        Assert.Equal(1, head[0].Attempts);
        Assert.True(wait > TimeSpan.Zero);
    }

    private void Enqueue(string id) => _outbox.Enqueue(new OutboxItem
    {
        EventId = id,
        Kind = OutboxKinds.JobEvent,
        Route = Agent.Contracts.Backup.ControlPlaneRoutes.JobEvents(id),
        Payload = $"{{\"event_id\":\"{id}\"}}",
        IsTerminal = true
    }, DateTimeOffset.UtcNow);

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch (Exception)
            {
                return;
            }

            var path = ctx.Request.Url!.AbsolutePath;
            _received.Enqueue((path, ctx.Request.Headers["Authorization"]));
            var id = path.Split('/')[^2];
            var status = _statusByEvent.TryGetValue(id, out var s) ? s : 200;
            ctx.Response.StatusCode = status;
            if (status >= 400)
            {
                // The CloudOrc API answers errors with JSON; "tunnel-" ids imitate an offline tunnel (HTML).
                var tunnel = id.StartsWith("tunnel-", StringComparison.Ordinal);
                ctx.Response.ContentType = tunnel ? "text/html" : "application/json";
                var bytes = System.Text.Encoding.UTF8.GetBytes(tunnel ? "<html>ERR_NGROK_3200 endpoint offline</html>" : """{"error":{"code":"REJECTED"}}""");
                ctx.Response.OutputStream.Write(bytes);
            }

            ctx.Response.Close();
        }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
