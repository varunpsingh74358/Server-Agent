using System.Net.WebSockets;
using CloudOrc.Agent.Contracts.Health;
using CloudOrc.Agent.Contracts.Identity;
using CloudOrc.ControlAgent.Backend;
using CloudOrc.ControlAgent.Configuration;
using CloudOrc.ControlAgent.Health;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudOrc.ControlAgent.Tests.Backend;

/// <summary>
/// Exercises the actual scenario this feature exists for: staying connected to more than
/// one backend at the same time (e.g. a real production backend plus a local development
/// tunnel), each as its own independent <see cref="BackendConnectionService"/> instance
/// sharing one <see cref="OutgoingMessageChannel"/> and one <see cref="ControlAgentHealthState"/>.
/// </summary>
public sealed class BackendConnectionServiceMultiTargetTests
{
    [Fact]
    public async Task TwoTargets_BothConnect_AndAggregateHealthReportsConnected()
    {
        await using var production = LoopbackWebSocketServer.Start();
        await using var devTunnel = LoopbackWebSocketServer.Start();

        var identity = new AgentIdentity { AgentId = "agent-multi", ServerId = "server-1", MachineId = "machine-1", MachineName = "HOST-1", AgentVersion = "0.0.0" };
        var health = new ControlAgentHealthState();
        var outgoing = new OutgoingMessageChannel(registerDefaultTarget: false);
        outgoing.RegisterTarget("production");
        outgoing.RegisterTarget("dev-tunnel");

        var sharedOptions = Options.Create(new BackendConnectionOptions
        {
            ConnectTimeoutSeconds = 5,
            ReconnectInitialDelaySeconds = 1,
            ReconnectMaximumDelaySeconds = 1
        });

        var productionService = new BackendConnectionService(
            sharedOptions, identity, outgoing,
            new WssCommandSource(Options.Create(new ControlAgentOptions()), NullLogger<WssCommandSource>.Instance),
            health, NullLogger<BackendConnectionService>.Instance,
            targetName: "production", targetUrl: production.Url, targetCredential: null);

        var devTunnelService = new BackendConnectionService(
            sharedOptions, identity, outgoing,
            new WssCommandSource(Options.Create(new ControlAgentOptions()), NullLogger<WssCommandSource>.Instance),
            health, NullLogger<BackendConnectionService>.Instance,
            targetName: "dev-tunnel", targetUrl: devTunnel.Url, targetCredential: null);

        var productionConnectionTask = production.WaitForNextConnectionAsync(TimeSpan.FromSeconds(30));
        var devTunnelConnectionTask = devTunnel.WaitForNextConnectionAsync(TimeSpan.FromSeconds(30));

        await productionService.StartAsync(CancellationToken.None);
        await devTunnelService.StartAsync(CancellationToken.None);

        var productionSocket = await productionConnectionTask;
        var devTunnelSocket = await devTunnelConnectionTask;

        using var helloCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await productionSocket.ReceiveAsync(new byte[4096], helloCts.Token);
        await devTunnelSocket.ReceiveAsync(new byte[4096], helloCts.Token);

        Assert.Equal(BackendConnectionState.Connected, health.Snapshot(TimeSpan.FromSeconds(30)).BackendConnectionState);

        await productionService.StopAsync(CancellationToken.None);
        await devTunnelService.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task TwoTargets_BroadcastMessage_IsDeliveredToBothConnectedSockets()
    {
        await using var production = LoopbackWebSocketServer.Start();
        await using var devTunnel = LoopbackWebSocketServer.Start();

        var identity = new AgentIdentity { AgentId = "agent-multi", ServerId = "server-1", MachineId = "machine-1", MachineName = "HOST-1", AgentVersion = "0.0.0" };
        var health = new ControlAgentHealthState();
        var outgoing = new OutgoingMessageChannel(registerDefaultTarget: false);
        outgoing.RegisterTarget("production");
        outgoing.RegisterTarget("dev-tunnel");

        var sharedOptions = Options.Create(new BackendConnectionOptions
        {
            ConnectTimeoutSeconds = 5,
            ReconnectInitialDelaySeconds = 1,
            ReconnectMaximumDelaySeconds = 1
        });

        var productionService = new BackendConnectionService(
            sharedOptions, identity, outgoing,
            new WssCommandSource(Options.Create(new ControlAgentOptions()), NullLogger<WssCommandSource>.Instance),
            health, NullLogger<BackendConnectionService>.Instance,
            targetName: "production", targetUrl: production.Url, targetCredential: null);

        var devTunnelService = new BackendConnectionService(
            sharedOptions, identity, outgoing,
            new WssCommandSource(Options.Create(new ControlAgentOptions()), NullLogger<WssCommandSource>.Instance),
            health, NullLogger<BackendConnectionService>.Instance,
            targetName: "dev-tunnel", targetUrl: devTunnel.Url, targetCredential: null);

        var productionConnectionTask = production.WaitForNextConnectionAsync(TimeSpan.FromSeconds(30));
        var devTunnelConnectionTask = devTunnel.WaitForNextConnectionAsync(TimeSpan.FromSeconds(30));

        await productionService.StartAsync(CancellationToken.None);
        await devTunnelService.StartAsync(CancellationToken.None);

        var productionSocket = await productionConnectionTask;
        var devTunnelSocket = await devTunnelConnectionTask;

        using var helloCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await productionSocket.ReceiveAsync(new byte[4096], helloCts.Token);
        await devTunnelSocket.ReceiveAsync(new byte[4096], helloCts.Token);

        // Simulates a COMMAND_RESULT/HEARTBEAT/TELEMETRY publisher, which only ever calls
        // the broadcast TryEnqueue - it must reach every currently-connected backend.
        Assert.True(outgoing.TryEnqueue("""{"type":"HEARTBEAT","agentId":"agent-multi"}"""));

        var productionBuffer = new byte[4096];
        var devTunnelBuffer = new byte[4096];
        using var receiveCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var productionResult = await productionSocket.ReceiveAsync(productionBuffer, receiveCts.Token);
        var devTunnelResult = await devTunnelSocket.ReceiveAsync(devTunnelBuffer, receiveCts.Token);

        var productionMessage = System.Text.Encoding.UTF8.GetString(productionBuffer, 0, productionResult.Count);
        var devTunnelMessage = System.Text.Encoding.UTF8.GetString(devTunnelBuffer, 0, devTunnelResult.Count);

        Assert.Contains("HEARTBEAT", productionMessage);
        Assert.Contains("HEARTBEAT", devTunnelMessage);

        await productionService.StopAsync(CancellationToken.None);
        await devTunnelService.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task OneTargetDrops_TheOtherTargetIsUnaffected_AndAggregateStaysConnected()
    {
        await using var production = LoopbackWebSocketServer.Start();
        await using var devTunnel = LoopbackWebSocketServer.Start();

        var identity = new AgentIdentity { AgentId = "agent-multi", ServerId = "server-1", MachineId = "machine-1", MachineName = "HOST-1", AgentVersion = "0.0.0" };
        var health = new ControlAgentHealthState();
        var outgoing = new OutgoingMessageChannel(registerDefaultTarget: false);
        outgoing.RegisterTarget("production");
        outgoing.RegisterTarget("dev-tunnel");

        var sharedOptions = Options.Create(new BackendConnectionOptions
        {
            ConnectTimeoutSeconds = 5,
            ReconnectInitialDelaySeconds = 1,
            ReconnectMaximumDelaySeconds = 1
        });

        var productionService = new BackendConnectionService(
            sharedOptions, identity, outgoing,
            new WssCommandSource(Options.Create(new ControlAgentOptions()), NullLogger<WssCommandSource>.Instance),
            health, NullLogger<BackendConnectionService>.Instance,
            targetName: "production", targetUrl: production.Url, targetCredential: null);

        var devTunnelService = new BackendConnectionService(
            sharedOptions, identity, outgoing,
            new WssCommandSource(Options.Create(new ControlAgentOptions()), NullLogger<WssCommandSource>.Instance),
            health, NullLogger<BackendConnectionService>.Instance,
            targetName: "dev-tunnel", targetUrl: devTunnel.Url, targetCredential: null);

        var productionConnectionTask = production.WaitForNextConnectionAsync(TimeSpan.FromSeconds(30));
        var devTunnelConnectionTask = devTunnel.WaitForNextConnectionAsync(TimeSpan.FromSeconds(30));

        await productionService.StartAsync(CancellationToken.None);
        await devTunnelService.StartAsync(CancellationToken.None);

        var productionSocket = await productionConnectionTask;
        var devTunnelSocket = await devTunnelConnectionTask;

        using var helloCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await productionSocket.ReceiveAsync(new byte[4096], helloCts.Token);
        await devTunnelSocket.ReceiveAsync(new byte[4096], helloCts.Token);

        // Abruptly kill only the production connection - dev-tunnel must keep running
        // completely independently, and the aggregate must still read Connected because
        // dev-tunnel is still up.
        productionSocket.Abort();

        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (health.Snapshot(TimeSpan.FromSeconds(30)).BackendConnectionState != BackendConnectionState.Connected && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.Equal(BackendConnectionState.Connected, health.Snapshot(TimeSpan.FromSeconds(30)).BackendConnectionState);

        await productionService.StopAsync(CancellationToken.None);
        await devTunnelService.StopAsync(CancellationToken.None);
    }
}
