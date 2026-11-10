using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.Agent.Contracts.Identity;
using CloudOrc.ControlAgent.Backend;
using CloudOrc.ControlAgent.Configuration;
using CloudOrc.ControlAgent.Enrollment;
using CloudOrc.ControlAgent.Health;
using CloudOrc.ControlAgent.Transport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudOrc.ControlAgent.Tests.Backend;

/// <summary>Real-socket tests of the CO-DEV-BKP-001 additions to the existing agent WebSocket.</summary>
public sealed class BackendConnectionBackupTransportTests
{
    private static readonly AgentIdentity Identity = new() { AgentId = "agent-t", ServerId = "srv", MachineId = "m", MachineName = "n", AgentVersion = "1.0.0", Credential = "cred" };

    private sealed class Router : ITypedCommandRouter
    {
        public ConcurrentQueue<string> Routed { get; } = new();

        public bool TryRoute(string json, string? commandType)
        {
            if (!BackupCommandTypes.IsTyped(commandType))
            {
                return false;
            }

            Routed.Enqueue(commandType!);
            return true;
        }
    }

    private sealed class FakeCapabilities : IAgentCapabilitiesProvider
    {
        public IReadOnlyList<string> Capabilities { get; } = [AgentCapabilities.BackupV1, AgentCapabilities.RestoreV1];

        public IReadOnlyDictionary<string, string>? EngineVersions { get; } = new Dictionary<string, string> { ["kopia"] = "0.23.1" };
    }

    private static BackendConnectionService Create(string url, Router router, BackendLinkState link, bool powerShellEnabled, string dataDir)
    {
        var controlOptions = Options.Create(new ControlAgentOptions { DataDirectory = dataDir, PowerShellExecEnabled = powerShellEnabled });
        var store = new EnrolledStateStore(controlOptions, NullLogger<EnrolledStateStore>.Instance);
        return new BackendConnectionService(
            Options.Create(new BackendConnectionOptions { Enabled = true, Url = url, ConnectTimeoutSeconds = 5, ReconnectInitialDelaySeconds = 1, ReconnectMaximumDelaySeconds = 1 }),
            Identity, new OutgoingMessageChannel(), new WssCommandSource(controlOptions, NullLogger<WssCommandSource>.Instance),
            new ControlAgentHealthState(), NullLogger<BackendConnectionService>.Instance,
            new AgentCredentialProvider(Identity, store), router, new FakeCapabilities(), null, link, controlOptions, store);
    }

    [Fact]
    public async Task Hello_CarriesCapabilityProfile_TypedCommandsAreRouted_PowerShellCanBeDisabled()
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        var router = new Router();
        var link = new BackendLinkState();
        var service = Create($"ws://localhost:{port}/agent", router, link, powerShellEnabled: false, Path.GetTempPath());

        var accept = listener.GetContextAsync();
        await service.StartAsync(CancellationToken.None);
        var context = await accept.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("Bearer cred", context.Request.Headers["Authorization"]);
        var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;

        var hello = JsonDocument.Parse(await ReceiveAsync(socket)).RootElement;
        Assert.Equal("HELLO", hello.GetProperty("type").GetString());
        Assert.Equal(BackupSchema.ProtocolVersion, hello.GetProperty("protocolVersion").GetString());
        Assert.Contains(AgentCapabilities.BackupV1, hello.GetProperty("capabilities").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("0.23.1", hello.GetProperty("engines").GetProperty("kopia").GetString());

        await SendAsync(socket, """{"type":"COMMAND","commandId":"c1","commandType":"backup.run","commandVersion":"1.0","parameters":{"jobId":"j","policyId":"p"}}""");
        await SendAsync(socket, """{"type":"COMMAND","commandId":"c2","commandType":"powershell-exec","parameters":{"script":"Get-Date"}}""");

        var error = await ReceiveUntilAsync(socket, m => m.Contains("\"ERROR\"", StringComparison.Ordinal));
        Assert.Contains("powershell-exec", error);
        Assert.Equal(["backup.run"], router.Routed);
        Assert.True(link.IsConnected);
        Assert.NotNull(link.LastContactAt);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Forbidden_IsTreatedAsRevoked_AndReconnectingStops()
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        var attempts = 0;
        using var serverCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!serverCts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); } catch (Exception) { return; }
                Interlocked.Increment(ref attempts);
                ctx.Response.StatusCode = 403;
                ctx.Response.Close();
            }
        });

        var link = new BackendLinkState();
        var service = Create($"ws://localhost:{port}/agent", new Router(), link, powerShellEnabled: true, Path.GetTempPath());
        await service.StartAsync(CancellationToken.None);

        // Only the first 403 is followed by a 1-hour wait in production; this test checks the
        // state transition and that the policy marks it revoked immediately.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (link.Authorization != AuthorizationHealth.Revoked && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        Assert.Equal(AuthorizationHealth.Revoked, link.Authorization);
        Assert.False(link.IsConnected);
        Assert.Equal(1, Volatile.Read(ref attempts)); // next attempt is an hour away, not a retry storm

        await service.StopAsync(CancellationToken.None);
        await serverCts.CancelAsync();
    }

    private static async Task SendAsync(WebSocket socket, string json) =>
        await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<string> ReceiveAsync(WebSocket socket)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new byte[65536];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cts.Token);
            ms.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static async Task<string> ReceiveUntilAsync(WebSocket socket, Func<string, bool> predicate)
    {
        for (var i = 0; i < 20; i++)
        {
            var message = await ReceiveAsync(socket);
            if (predicate(message))
            {
                return message;
            }
        }

        throw new TimeoutException("Expected message not received.");
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
