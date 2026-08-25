using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CloudOrc.Agent.Contracts.Commands;
using CloudOrc.Agent.Contracts.Health;
using CloudOrc.Agent.Contracts.Identity;
using CloudOrc.Agent.Contracts.Protocol;
using CloudOrc.ControlAgent.Configuration;
using CloudOrc.ControlAgent.Health;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudOrc.ControlAgent.Backend;

/// <summary>
/// Owns exactly one outbound WebSocket connection to one backend target: connect, send
/// HELLO, run the send/receive loops, and reconnect with backoff on any failure. One
/// instance is created per configured <see cref="ResolvedBackendTarget"/> (see
/// Program.cs) - e.g. one for the primary/enrolled production backend and one more per
/// entry in <see cref="BackendConnectionOptions.AdditionalTargets"/> (a local development
/// tunnel, for example) - so the agent can stay connected to several backends at once.
/// Each instance is the ONLY thing that calls <see cref="ClientWebSocket.SendAsync"/> for
/// its own socket - everything else (heartbeat, telemetry, status, results) publishes
/// through the shared <see cref="OutgoingMessageChannel"/> instead, which broadcasts to
/// every target's queue and is what keeps sending thread-safe across connections.
///
/// A disconnected/failed backend connection is logged and retried - it never throws out
/// of <see cref="ExecuteAsync"/>, so it can never crash the Control Agent, interrupt local
/// file command processing, or affect any other target's connection, all of which run
/// entirely independently.
/// </summary>
public sealed class BackendConnectionService(
    IOptions<BackendConnectionOptions> options,
    AgentIdentity identity,
    OutgoingMessageChannel outgoing,
    WssCommandSource commandSource,
    ControlAgentHealthState health,
    ILogger<BackendConnectionService> logger,
    string targetName = OutgoingMessageChannel.DefaultTargetName,
    string? targetUrl = null,
    string? targetCredential = null) : BackgroundService
{
    private readonly BackendConnectionOptions _options = options.Value;
    private readonly string _url = targetUrl ?? options.Value.Url;
    private readonly string? _credential = targetCredential ?? identity.Credential;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoff = new ReconnectBackoffCalculator(_options);

        logger.LogInformation(
            "Backend connection worker starting for target '{TargetName}'. Url: {Url} (agentId={AgentId}, serverId={ServerId}).",
            targetName, _url, identity.AgentId, identity.ServerId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOneConnectionAsync(backoff, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Backend connection attempt to target '{TargetName}' failed.", targetName);
            }

            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            var delay = backoff.NextDelay();
            health.SetBackendConnectionState(targetName, BackendConnectionState.Reconnecting);
            logger.LogInformation("Reconnecting to target '{TargetName}' in {DelaySeconds:F0}s.", targetName, delay.TotalSeconds);

            try
            {
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        health.SetBackendConnectionState(targetName, BackendConnectionState.Disabled);
        logger.LogInformation("Backend connection worker for target '{TargetName}' stopped.", targetName);
    }

    private async Task RunOneConnectionAsync(ReconnectBackoffCalculator backoff, CancellationToken stoppingToken)
    {
        using var socket = new ClientWebSocket();
        health.SetBackendConnectionState(targetName, BackendConnectionState.Connecting);

        // Present this target's permanent credential (never a one-time enrollment
        // secret) on the handshake itself, before any protocol message is exchanged.
        // Absent entirely for a non-enrolled, local-testing target - the backend/test
        // server side decides whether to require it.
        if (!string.IsNullOrEmpty(_credential))
        {
            socket.Options.SetRequestHeader("Authorization", $"Bearer {_credential}");
        }

        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
        {
            connectCts.CancelAfter(TimeSpan.FromSeconds(_options.ConnectTimeoutSeconds));
            try
            {
                await socket.ConnectAsync(new Uri(_url), connectCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // Distinguish "our own connect timeout fired" from "app is shutting down"
                // (both surface as OperationCanceledException) before classifying, so a
                // hung/unreachable backend gets a specific, actionable diagnostic instead
                // of a bare TaskCanceledException stack trace.
                var wasConnectTimeout = connectCts.IsCancellationRequested;
                var reason = ConnectionFailureClassifier.Classify(ex, wasConnectTimeout);
                logger.LogWarning("Could not connect to target '{TargetName}' at {Url}: {Reason}", targetName, _url, reason);
                throw;
            }
        }

        logger.LogInformation("Connected to target '{TargetName}' at {Url}.", targetName, _url);
        health.SetBackendConnectionState(targetName, BackendConnectionState.Connected);
        backoff.Reset();

        await SendHelloAsync(socket, stoppingToken).ConfigureAwait(false);

        var receiveTask = ReceiveLoopAsync(socket, stoppingToken);
        var sendTask = SendLoopAsync(socket, stoppingToken);

        var finished = await Task.WhenAny(receiveTask, sendTask).ConfigureAwait(false);

        try
        {
            await finished.ConfigureAwait(false);
        }
        finally
        {
            health.SetBackendConnectionState(targetName, BackendConnectionState.Disconnected);
            await CloseQuietlyAsync(socket).ConfigureAwait(false);
        }

        // Observe the still-running loop's completion/exception without letting it escape
        // as an unobserved task exception.
        var other = ReferenceEquals(finished, receiveTask) ? sendTask : receiveTask;
        try
        {
            await other.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Secondary connection loop ended after the primary one closed the connection.");
        }
    }

    private async Task SendHelloAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var hello = new HelloMessage
        {
            AgentId = identity.AgentId,
            ServerId = identity.ServerId,
            MachineId = identity.MachineId,
            MachineName = identity.MachineName,
            AgentVersion = identity.AgentVersion
        };

        var json = JsonSerializer.Serialize(hello, ProtocolJson.Options);
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Sent HELLO (agentId={AgentId}, machineId={MachineId}).", identity.AgentId, identity.MachineId);
    }

    private async Task SendLoopAsync(ClientWebSocket socket, CancellationToken stoppingToken)
    {
        await foreach (var json in outgoing.ReadAllAsync(targetName, stoppingToken).ConfigureAwait(false))
        {
            if (socket.State != WebSocketState.Open)
            {
                break;
            }

            var bytes = Encoding.UTF8.GetBytes(json);
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken stoppingToken)
    {
        var buffer = new byte[8192];

        while (socket.State == WebSocketState.Open && !stoppingToken.IsCancellationRequested)
        {
            using var messageStream = new MemoryStream();
            WebSocketReceiveResult receiveResult;

            do
            {
                receiveResult = await socket.ReceiveAsync(buffer, stoppingToken).ConfigureAwait(false);

                if (receiveResult.MessageType == WebSocketMessageType.Close)
                {
                    logger.LogInformation("Backend closed the connection ({CloseStatus}: {CloseDescription}).", receiveResult.CloseStatus, receiveResult.CloseStatusDescription);
                    return;
                }

                messageStream.Write(buffer, 0, receiveResult.Count);
            }
            while (!receiveResult.EndOfMessage);

            var json = Encoding.UTF8.GetString(messageStream.ToArray());
            HandleIncomingMessage(json);
        }
    }

    private void HandleIncomingMessage(string json)
    {
        var type = ProtocolJson.TryReadMessageType(json);

        try
        {
            switch (type)
            {
                case ProtocolMessageTypes.Command:
                    HandleCommandMessage(json);
                    break;

                case ProtocolMessageTypes.Ping:
                    HandlePing();
                    break;

                default:
                    logger.LogWarning("Received an unrecognized or malformed backend message (type={Type}); ignoring it.", type ?? "(none)");
                    break;
            }
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse an incoming {Type} message; ignoring it.", type ?? "(unknown)");
        }
    }

    private const string PowerShellExecCommandType = "powershell-exec";

    private void HandleCommandMessage(string json)
    {
        var message = JsonSerializer.Deserialize<CommandMessage>(json, ProtocolJson.Options);
        if (message?.Parameters is null)
        {
            logger.LogWarning("Received a COMMAND message with no parameters payload; ignoring it.");
            return;
        }

        if (!string.Equals(message.CommandType, PowerShellExecCommandType, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "Rejected incoming command {CommandId}: unsupported commandType '{CommandType}'.",
                message.CommandId, message.CommandType);

            var typeError = new ErrorMessage
            {
                Message = $"Unsupported commandType '{message.CommandType}'. Only '{PowerShellExecCommandType}' is supported.",
                RelatedCommandId = message.CommandId,
                CorrelationId = message.CorrelationId
            };
            outgoing.TryEnqueueTo(targetName, JsonSerializer.Serialize(typeError, ProtocolJson.Options));
            return;
        }

        var request = new CommandRequest
        {
            CommandId = message.CommandId,
            CorrelationId = message.CorrelationId,
            CommandType = message.CommandType,
            Script = message.Parameters.Script,
            TimeoutSeconds = message.Parameters.TimeoutSeconds,
            CreatedAt = message.CreatedAt
        };

        if (!commandSource.TryAcceptIncomingCommand(request, out var rejectionReason))
        {
            logger.LogWarning("Rejected incoming command {CommandId}: {Reason}", request.CommandId, rejectionReason);

            var error = new ErrorMessage
            {
                Message = rejectionReason ?? "Command rejected.",
                RelatedCommandId = request.CommandId,
                CorrelationId = request.CorrelationId
            };
            outgoing.TryEnqueueTo(targetName, JsonSerializer.Serialize(error, ProtocolJson.Options));
        }
    }

    private void HandlePing()
    {
        var snapshot = health.Snapshot(TimeSpan.FromSeconds(30));
        var heartbeat = new HeartbeatMessage
        {
            AgentId = identity.AgentId,
            ServerId = identity.ServerId,
            Status = snapshot.Status,
            WorkerAlive = snapshot.DetectionWorkerAlive && snapshot.ProcessingWorkerAlive,
            CurrentCommandId = snapshot.CurrentCommandId,
            CurrentCommandStatus = snapshot.CurrentCommandStatus,
            LastActivityAt = snapshot.LastProcessingActivityAt > snapshot.LastDetectionActivityAt
                ? snapshot.LastProcessingActivityAt
                : snapshot.LastDetectionActivityAt
        };

        outgoing.TryEnqueueTo(targetName, JsonSerializer.Serialize(heartbeat, ProtocolJson.Options));
    }

    private static async Task CloseQuietlyAsync(ClientWebSocket socket)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Reconnecting", closeCts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Best-effort close only - the socket is being disposed regardless.
        }
    }
}
