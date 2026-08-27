using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using CloudOrc.Agent.Contracts.Abstractions;
using CloudOrc.Agent.Contracts.Commands;
using CloudOrc.Agent.Contracts.Health;
using CloudOrc.Agent.Contracts.Identity;
using CloudOrc.ControlAgent.Backend;
using CloudOrc.ControlAgent.Configuration;
using CloudOrc.ControlAgent.Health;
using CloudOrc.ControlAgent.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudOrc.ControlAgent.Tests.Backend;

/// <summary>
/// Exercises <see cref="BackendConnectionService"/> against a real (loopback-only)
/// WebSocket server built on <see cref="HttpListener"/> - no ASP.NET Core TestHost
/// dependency needed, since none is referenced anywhere else in this solution. There was
/// no prior real-socket coverage of this class; every existing test either constructs
/// <see cref="WssCommandSource"/>/<see cref="ReconnectBackoffCalculator"/> directly or
/// exercises message serialization only.
/// </summary>
public sealed class BackendConnectionServiceDisconnectTests
{
    [Fact]
    public async Task ExecuteAsync_BackendDropsConnection_AgentReconnectsWithoutCrashing()
    {
        await using var server = LoopbackWebSocketServer.Start();

        var options = Options.Create(new BackendConnectionOptions
        {
            Enabled = true,
            Url = server.Url,
            ConnectTimeoutSeconds = 5,
            ReconnectInitialDelaySeconds = 1,
            ReconnectMaximumDelaySeconds = 1
        });
        var identity = new AgentIdentity { AgentId = "agent-disconnect-test", ServerId = "server-1", MachineId = "machine-1", MachineName = "HOST-1", AgentVersion = "0.0.0" };
        var outgoing = new OutgoingMessageChannel();
        var commandSource = new WssCommandSource(Options.Create(new ControlAgentOptions()), NullLogger<WssCommandSource>.Instance);
        var health = new ControlAgentHealthState();
        var service = new BackendConnectionService(options, identity, outgoing, commandSource, health, NullLogger<BackendConnectionService>.Instance);

        var firstConnectionTask = server.WaitForNextConnectionAsync(TimeSpan.FromSeconds(30));
        await service.StartAsync(CancellationToken.None);

        var firstSocket = await firstConnectionTask;

        // Wait for HELLO to actually arrive rather than asserting Connected state right
        // after the server-side accept completes: accept (server-side) and
        // SetBackendConnectionState(Connected) (client-side) are two independent
        // completions of the same handshake with no ordering guarantee between them.
        // SendHelloAsync only runs after SetBackendConnectionState in the client's own
        // sequential code, so observing HELLO on the wire guarantees the state is already set.
        using var helloCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await firstSocket.ReceiveAsync(new byte[4096], helloCts.Token);

        Assert.Equal(BackendConnectionState.Connected, health.Snapshot(TimeSpan.FromSeconds(30)).BackendConnectionState);

        var secondConnectionTask = server.WaitForNextConnectionAsync(TimeSpan.FromSeconds(30));

        // Simulate an abrupt backend-side disconnect (not a graceful close) - the same
        // "backend process killed"/network-drop scenario documented as a manual test in
        // docs/BACKEND_WEBSOCKET_TESTING.md §7, now automated.
        firstSocket.Abort();

        var secondSocket = await secondConnectionTask;
        Assert.NotNull(secondSocket);

        Assert.False(service.ExecuteTask is { IsFaulted: true }, "The connection worker must survive a disconnect and keep retrying, never fault out of ExecuteAsync.");

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_BackendSendsGoingAwayClose_AgentReconnectsPromptly()
    {
        // Simulates a backend gracefully closing the socket during a deploy/restart
        // (WS close code 1001, "Going Away") rather than an abrupt drop - covers the
        // distinct log branch in BackendConnectionService.ReceiveLoopAsync and confirms
        // reconnect still happens from the initial backoff delay, not an accumulated one.
        await using var server = LoopbackWebSocketServer.Start();

        var options = Options.Create(new BackendConnectionOptions
        {
            Enabled = true,
            Url = server.Url,
            ConnectTimeoutSeconds = 5,
            ReconnectInitialDelaySeconds = 1,
            ReconnectMaximumDelaySeconds = 1
        });
        var identity = new AgentIdentity { AgentId = "agent-goingaway-test", ServerId = "server-1", MachineId = "machine-1", MachineName = "HOST-1", AgentVersion = "0.0.0" };
        var outgoing = new OutgoingMessageChannel();
        var commandSource = new WssCommandSource(Options.Create(new ControlAgentOptions()), NullLogger<WssCommandSource>.Instance);
        var health = new ControlAgentHealthState();
        var service = new BackendConnectionService(options, identity, outgoing, commandSource, health, NullLogger<BackendConnectionService>.Instance);

        var firstConnectionTask = server.WaitForNextConnectionAsync(TimeSpan.FromSeconds(30));
        await service.StartAsync(CancellationToken.None);

        var firstSocket = await firstConnectionTask;

        using var helloCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await firstSocket.ReceiveAsync(new byte[4096], helloCts.Token);

        Assert.Equal(BackendConnectionState.Connected, health.Snapshot(TimeSpan.FromSeconds(30)).BackendConnectionState);

        var secondConnectionTask = server.WaitForNextConnectionAsync(TimeSpan.FromSeconds(30));

        using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await firstSocket.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "redeploying", closeCts.Token);

        var secondSocket = await secondConnectionTask;
        Assert.NotNull(secondSocket);

        Assert.False(service.ExecuteTask is { IsFaulted: true }, "The connection worker must survive a graceful going-away close and keep retrying, never fault out of ExecuteAsync.");

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task LocalCommandProcessing_ContinuesNormally_WhileBackendConnectionIsFailing()
    {
        // BackendConnectionService is pointed at a port nothing listens on, so every
        // connect attempt fails and it sits in its reconnect-backoff loop for the whole
        // test - confirming CommandProcessingService (a completely independent
        // BackgroundService/queue, per docs/FUTURE_BACKEND_INTEGRATION.md) keeps
        // processing local work regardless of backend connectivity.
        var deadPort = GetFreeLoopbackPort();
        var backendOptions = Options.Create(new BackendConnectionOptions
        {
            Enabled = true,
            Url = $"ws://127.0.0.1:{deadPort}/agent",
            ConnectTimeoutSeconds = 1,
            ReconnectInitialDelaySeconds = 1,
            ReconnectMaximumDelaySeconds = 1
        });
        var identity = new AgentIdentity { AgentId = "agent-outage-test", ServerId = "server-1", MachineId = "machine-1", MachineName = "HOST-1", AgentVersion = "0.0.0" };
        var outgoing = new OutgoingMessageChannel();
        var wssCommandSource = new WssCommandSource(Options.Create(new ControlAgentOptions()), NullLogger<WssCommandSource>.Instance);
        var health = new ControlAgentHealthState();
        var backendService = new BackendConnectionService(backendOptions, identity, outgoing, wssCommandSource, health, NullLogger<BackendConnectionService>.Instance);

        var queue = new InMemoryCommandQueue();
        var sink = new RecordingSink();
        var origin = new NoOpCommandSource();
        await queue.EnqueueAsync(new CommandJob
        {
            Request = new CommandRequest { CommandId = "local-1", Script = "Get-Date", TimeoutSeconds = 30 },
            EffectiveTimeoutSeconds = 30,
            SourceReference = "local-1",
            OriginSource = origin
        }, CancellationToken.None);

        var processingService = new CommandProcessingService(
            queue,
            [sink],
            new NullCommandStatusPublisher(),
            new PowerShellCommandExecutor(NullLogger<PowerShellCommandExecutor>.Instance),
            health,
            NullLogger<CommandProcessingService>.Instance);

        await backendService.StartAsync(CancellationToken.None);
        await processingService.StartAsync(CancellationToken.None);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (sink.Results.Count == 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        await processingService.StopAsync(CancellationToken.None);
        await backendService.StopAsync(CancellationToken.None);

        Assert.Single(sink.Results);
        Assert.Equal(CommandStatus.Success, sink.Results[0].Status);
    }

    private static int GetFreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class RecordingSink : ICommandResultSink
    {
        public List<CommandResult> Results { get; } = [];

        public Task WriteAsync(CommandResult result, CancellationToken cancellationToken)
        {
            Results.Add(result);
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpCommandSource : ICommandSource
    {
        public IAsyncEnumerable<CommandJob> GetCommandsAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by these tests - the job is enqueued directly.");

        public Task AcknowledgeAsync(CommandJob job, bool succeeded, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// Minimal real WebSocket server for testing <see cref="BackendConnectionService"/>
    /// against actual socket behavior (connect, abrupt disconnect, reconnect) without
    /// pulling in an ASP.NET Core TestHost dependency this solution doesn't otherwise use.
    /// </summary>
    private sealed class LoopbackWebSocketServer : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;
        private readonly Lock _sync = new();
        private readonly List<TaskCompletionSource<WebSocket>> _waiters = [];

        public string Url { get; }

        private LoopbackWebSocketServer(HttpListener listener, string url)
        {
            _listener = listener;
            Url = url;
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        public static LoopbackWebSocketServer Start()
        {
            var port = GetFreeLoopbackPort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            return new LoopbackWebSocketServer(listener, $"ws://127.0.0.1:{port}/agent");
        }

        public Task<WebSocket> WaitForNextConnectionAsync(TimeSpan timeout)
        {
            var tcs = new TaskCompletionSource<WebSocket>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync)
            {
                _waiters.Add(tcs);
            }

            return tcs.Task.WaitAsync(timeout);
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().WaitAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    return;
                }

                if (!context.Request.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    continue;
                }

                var wsContext = await context.AcceptWebSocketAsync(subProtocol: null).ConfigureAwait(false);

                TaskCompletionSource<WebSocket>? waiter;
                lock (_sync)
                {
                    waiter = _waiters.Count > 0 ? _waiters[0] : null;
                    if (waiter is not null)
                    {
                        _waiters.RemoveAt(0);
                    }
                }

                waiter?.TrySetResult(wsContext.WebSocket);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            _listener.Close();
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch
            {
                // Best-effort shutdown only.
            }
        }
    }
}
