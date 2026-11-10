using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.Agent.Contracts.Commands;
using CloudOrc.Agent.Contracts.Health;
using CloudOrc.Agent.Contracts.Identity;
using CloudOrc.Agent.Contracts.Protocol;
using CloudOrc.ControlAgent.Configuration;
using CloudOrc.ControlAgent.Health;
using CloudOrc.ControlAgent.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudOrc.ControlAgent.Backend;

/// <summary>
/// Owns the agent's single outbound WebSocket connection to the backend: connect, send
/// HELLO, run the send/receive loops, and reconnect with backoff on any failure. This is
/// the ONLY place that calls <see cref="ClientWebSocket.SendAsync"/> - everything else
/// (heartbeat, telemetry, status, results) publishes through <see cref="OutgoingMessageChannel"/>
/// instead, which is what keeps sending thread-safe.
///
/// A disconnected/failed backend connection is logged and retried - it never throws out
/// of <see cref="ExecuteAsync"/>, so it can never crash the Control Agent or interrupt
/// local file command processing, which runs entirely independently.
///
/// CO-DEV-BKP-001 additions (same connection, same enrollment flow - nothing new to connect to):
/// HELLO carries protocolVersion + capability profile (section 3 version negotiation); typed backup/
/// restore/agent commands are handed to <see cref="ITypedCommandRouter"/> instead of the
/// sequential script queue; the handshake's HTTP status drives reconnect behaviour (section 8: 401 ->
/// session renewal, 403 -> revoked and never retried indefinitely, 429/503 -> Retry-After); the
/// current (renewable) credential and an optional mTLS client certificate are presented.
/// </summary>
public sealed class BackendConnectionService : BackgroundService
{
    private readonly BackendConnectionOptions _options;
    private readonly AgentIdentity identity;
    private readonly OutgoingMessageChannel outgoing;
    private readonly WssCommandSource commandSource;
    private readonly ControlAgentHealthState health;
    private readonly ILogger<BackendConnectionService> logger;
    private readonly AgentCredentialProvider? _credentials;
    private readonly ITypedCommandRouter? _typedRouter;
    private readonly IAgentCapabilitiesProvider? _capabilities;
    private readonly ISessionRenewer? _renewer;
    private readonly BackendLinkState _link;
    private readonly bool _powerShellExecEnabled;
    private readonly Enrollment.EnrolledStateStore? _enrolledStateStore;
    private readonly ConnectionRetryPolicy _retryPolicy = new();
    private TaskCompletionSource _reconnectSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Minimal constructor (local testing / existing tests): no typed commands, no renewal.</summary>
    public BackendConnectionService(
        IOptions<BackendConnectionOptions> options,
        AgentIdentity identity,
        OutgoingMessageChannel outgoing,
        WssCommandSource commandSource,
        ControlAgentHealthState health,
        ILogger<BackendConnectionService> logger)
        : this(options, identity, outgoing, commandSource, health, logger, null, null, null, null, new BackendLinkState(), null, null)
    {
    }

    public BackendConnectionService(
        IOptions<BackendConnectionOptions> options,
        AgentIdentity identity,
        OutgoingMessageChannel outgoing,
        WssCommandSource commandSource,
        ControlAgentHealthState health,
        ILogger<BackendConnectionService> logger,
        AgentCredentialProvider? credentials,
        ITypedCommandRouter? typedRouter,
        IAgentCapabilitiesProvider? capabilities,
        ISessionRenewer? renewer,
        BackendLinkState link,
        IOptions<ControlAgentOptions>? controlAgentOptions,
        Enrollment.EnrolledStateStore? enrolledStateStore)
    {
        _options = options.Value;
        this.identity = identity;
        this.outgoing = outgoing;
        this.commandSource = commandSource;
        this.health = health;
        this.logger = logger;
        _credentials = credentials;
        _typedRouter = typedRouter;
        _capabilities = capabilities;
        _renewer = renewer;
        _link = link;
        _powerShellExecEnabled = controlAgentOptions?.Value.PowerShellExecEnabled ?? true;
        _enrolledStateStore = enrolledStateStore;

        _link.ReconnectRequested += () => _reconnectSignal.TrySetResult();
    }

    /// <summary>Thrown internally when the WebSocket handshake was answered with an HTTP error.</summary>
    private sealed class HandshakeRejectedException(HttpStatusCode? status, TimeSpan? retryAfter, Exception inner)
        : Exception(inner.Message, inner)
    {
        public HttpStatusCode? Status { get; } = status;

        public TimeSpan? RetryAfter { get; } = retryAfter;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoff = new ReconnectBackoffCalculator(_options);

        logger.LogInformation(
            "Backend connection worker starting. Target: {Url} (agentId={AgentId}, serverId={ServerId}).",
            _options.Url, identity.AgentId, identity.ServerId);

        while (!stoppingToken.IsCancellationRequested)
        {
            ReconnectDecision decision;
            try
            {
                await RunOneConnectionAsync(backoff, stoppingToken).ConfigureAwait(false);
                // A planned backend restart (GOING_AWAY) tells us when to come back: honour it so a
                // fleet does not reconnect all at once (§8 jitter / Retry-After).
                decision = new ReconnectDecision(_serverRequestedDelay, false, false, _retryPolicy.Authorization, "Connection closed - reconnecting");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (HandshakeRejectedException ex)
            {
                decision = _retryPolicy.OnFailure(ex.Status, ex.RetryAfter, _renewer?.IsAvailable ?? false);
                logger.LogWarning("Backend rejected the connection (HTTP {Status}): {RetryStatus}.", (int?)ex.Status, decision.RetryStatus);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Backend connection attempt failed.");
                decision = _retryPolicy.OnFailure(null, null, false);
            }

            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            if (decision.AttemptSessionRenewal && _renewer is not null)
            {
                var outcome = await _renewer.RenewAsync(stoppingToken).ConfigureAwait(false);
                logger.LogInformation("Session renewal after HTTP 401: {Outcome}.", outcome);
            }

            if (decision.StopReconnecting)
            {
                // Section 8 "Never retry revoked authorization indefinitely": park until a local
                // Retry (tray) or a re-activation restarts us - no further traffic to the backend.
                _link.MarkDisconnected(decision.RetryStatus, decision.Authorization, stopped: true);
                health.SetBackendConnectionState(BackendConnectionState.Disconnected);
                logger.LogError("Reconnecting stopped: {Reason}. A local Retry or re-activation is required.", decision.RetryStatus);
                await WaitForReconnectSignalAsync(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
                backoff.Reset();
                continue;
            }

            var delay = decision.Delay ?? backoff.NextDelay();
            _link.MarkDisconnected(decision.RetryStatus, decision.Authorization, stopped: false);
            health.SetBackendConnectionState(BackendConnectionState.Reconnecting);
            logger.LogInformation("Reconnecting to backend in {DelaySeconds:F0}s.", delay.TotalSeconds);

            await WaitForReconnectSignalAsync(delay, stoppingToken).ConfigureAwait(false);
        }

        health.SetBackendConnectionState(BackendConnectionState.Disabled);
        logger.LogInformation("Backend connection worker stopped.");
    }

    /// <summary>Waits for <paramref name="delay"/>, or less if a local Retry is requested.</summary>
    private async Task WaitForReconnectSignalAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        var signal = _reconnectSignal;
        try
        {
            await Task.WhenAny(Task.Delay(delay, stoppingToken), signal.Task).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        if (signal.Task.IsCompleted)
        {
            _reconnectSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private async Task RunOneConnectionAsync(ReconnectBackoffCalculator backoff, CancellationToken stoppingToken)
    {
        using var socket = new ClientWebSocket();
        health.SetBackendConnectionState(BackendConnectionState.Connecting);

        // Needed to read the handshake's HTTP status/Retry-After when the backend refuses us.
        socket.Options.CollectHttpResponseDetails = true;

        // Present the current session credential (renewable; never the one-time enrollment
        // secret) on the handshake itself. Absent entirely for a non-enrolled, local-testing
        // agent - the backend/test server side decides whether to require it.
        var credential = _credentials?.Current ?? identity.Credential;
        if (!string.IsNullOrEmpty(credential))
        {
            socket.Options.SetRequestHeader("Authorization", $"Bearer {credential}");
        }

        // Looked up per connection so a rotated mTLS certificate is used from the next reconnect on.
        if (OperatingSystem.IsWindows() && _enrolledStateStore is not null
            && DeviceKeyStore.FindClientCertificate(_enrolledStateStore.TryLoad()?.ClientCertificateThumbprint) is { } clientCertificate)
        {
            socket.Options.ClientCertificates.Add(clientCertificate);
        }

        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
        {
            connectCts.CancelAfter(TimeSpan.FromSeconds(_options.ConnectTimeoutSeconds));
            try
            {
                await socket.ConnectAsync(new Uri(_options.Url), connectCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // Distinguish "our own connect timeout fired" from "app is shutting down"
                // (both surface as OperationCanceledException) before classifying, so a
                // hung/unreachable backend gets a specific, actionable diagnostic instead
                // of a bare TaskCanceledException stack trace.
                var wasConnectTimeout = connectCts.IsCancellationRequested;
                var reason = ConnectionFailureClassifier.Classify(ex, wasConnectTimeout);
                logger.LogWarning("Could not connect to backend at {Url}: {Reason}", _options.Url, reason);

                var status = socket.HttpStatusCode;
                if (status != 0 && status != HttpStatusCode.SwitchingProtocols)
                {
                    throw new HandshakeRejectedException(status, RetryAfter.FromHeaders(socket.HttpResponseHeaders, DateTimeOffset.UtcNow), ex);
                }

                throw;
            }
        }

        logger.LogInformation("Connected to backend at {Url}.", _options.Url);
        health.SetBackendConnectionState(BackendConnectionState.Connected);
        backoff.Reset();
        // The CloudOrc backend authenticates on HELLO (not on the HTTP upgrade), so the retry
        // policy's counters are only reset once the backend actually accepted us - see
        // ReceiveLoopAsync. Otherwise repeated 4401/4403 closes would never escalate (§8).
        _helloAccepted = false;
        _serverRequestedDelay = null;
        _link.MarkConnected(DateTimeOffset.UtcNow);

        await SendHelloAsync(socket, stoppingToken).ConfigureAwait(false);

        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var receiveTask = ReceiveLoopAsync(socket, connectionCts.Token);
        var sendTask = SendLoopAsync(socket, connectionCts.Token);

        var finished = await Task.WhenAny(receiveTask, sendTask).ConfigureAwait(false);

        // Whichever loop is still running reacts to the connection ending only on its
        // own schedule - SendLoopAsync in particular can be parked on an empty outgoing
        // channel indefinitely, with nothing about a closed socket to wake it up until
        // the next heartbeat/telemetry message happens to be queued. Cancel explicitly
        // here so the other loop unblocks immediately: without this, a graceful backend
        // close (the recommended way to signal a planned deploy/restart, as opposed to
        // an abrupt drop) would delay this method's return - and therefore the next
        // reconnect attempt - by up to a full heartbeat interval, or hang indefinitely
        // if nothing is ever queued.
        connectionCts.Cancel();

        try
        {
            await finished.ConfigureAwait(false);
        }
        finally
        {
            health.SetBackendConnectionState(BackendConnectionState.Disconnected);
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
            AgentVersion = identity.AgentVersion,
            ProtocolVersion = _capabilities is null ? null : BackupSchema.ProtocolVersion,
            Capabilities = _capabilities?.Capabilities,
            Engines = _capabilities?.EngineVersions
        };

        var json = JsonSerializer.Serialize(hello, ProtocolJson.Options);
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Sent HELLO (agentId={AgentId}, machineId={MachineId}).", identity.AgentId, identity.MachineId);
    }

    private async Task SendLoopAsync(ClientWebSocket socket, CancellationToken stoppingToken)
    {
        await foreach (var json in outgoing.ReadAllAsync(stoppingToken).ConfigureAwait(false))
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
                    // Backend authorization/availability verdicts arrive as WebSocket close codes
                    // after HELLO (the HTTP upgrade itself always succeeds). Map them onto the same
                    // HTTP-status retry policy the handshake path uses.
                    var mapped = MapCloseCode((int?)receiveResult.CloseStatus);
                    if (mapped is not null)
                    {
                        var retryAfter = mapped == HttpStatusCode.Forbidden ? null : _serverRequestedDelay;
                        throw new HandshakeRejectedException(
                            mapped,
                            retryAfter,
                            new InvalidOperationException($"Backend closed the connection with {(int?)receiveResult.CloseStatus}: {receiveResult.CloseStatusDescription}"));
                    }

                    // EndpointUnavailable (WS close code 1001, "Going Away") is the
                    // conventional signal a server sends while shutting down for a
                    // deploy/restart, as opposed to a protocol error or policy violation.
                    // Called out distinctly so a deploy-caused disconnect is obviously
                    // benign in the log, not confused with a real backend problem -
                    // either way the reconnect loop in ExecuteAsync retries automatically
                    // starting from the initial backoff delay (backoff.Reset() already
                    // ran for this connection), not an accumulated one.
                    if (receiveResult.CloseStatus == WebSocketCloseStatus.EndpointUnavailable)
                    {
                        logger.LogInformation(
                            "Backend is going away ({CloseDescription}) - likely a planned restart/deployment. Reconnecting automatically.",
                            receiveResult.CloseStatusDescription);
                    }
                    else
                    {
                        logger.LogInformation("Backend closed the connection ({CloseStatus}: {CloseDescription}).", receiveResult.CloseStatus, receiveResult.CloseStatusDescription);
                    }

                    return;
                }

                messageStream.Write(buffer, 0, receiveResult.Count);
            }
            while (!receiveResult.EndOfMessage);

            var json = Encoding.UTF8.GetString(messageStream.ToArray());
            _link.MarkContact(DateTimeOffset.UtcNow);
            HandleIncomingMessage(json);
        }
    }

    // WebSocket close codes used by the CloudOrc backend gateway (agentConnectionRegistry.js).
    internal const int CloseUnauthorized = 4401;
    internal const int CloseRevoked = 4403;
    internal const int CloseTryAgain = 1013;

    private volatile bool _helloAccepted;
    private TimeSpan? _serverRequestedDelay;

    /// <summary>Close code → equivalent HTTP status for <see cref="ConnectionRetryPolicy"/>; null = ordinary close.</summary>
    internal static HttpStatusCode? MapCloseCode(int? closeCode) => closeCode switch
    {
        CloseUnauthorized => HttpStatusCode.Unauthorized,
        CloseRevoked => HttpStatusCode.Forbidden,
        CloseTryAgain => HttpStatusCode.ServiceUnavailable,
        _ => null
    };

    /// <summary>HELLO_NACK / GOING_AWAY carry the backend's verdict and when to come back.</summary>
    private void HandleControlNotice(string json, bool isNack)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("reconnectAfterMs", out var after) && after.ValueKind == JsonValueKind.Number
            && after.TryGetInt64(out var ms) && ms > 0)
        {
            _serverRequestedDelay = TimeSpan.FromMilliseconds(Math.Min(ms, (long)TimeSpan.FromHours(1).TotalMilliseconds));
        }

        var code = root.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : (int?)null;
        var error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString()
            : root.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
        if (isNack)
        {
            logger.LogWarning("Backend refused HELLO (code {Code}): {Error}.", code, error ?? "-");
        }
        else
        {
            logger.LogInformation("Backend is going away (code {Code}): {Reason}; reconnect in {Delay}.", code, error ?? "-", _serverRequestedDelay);
        }
    }

    private void HandleIncomingMessage(string json)
    {
        var type = ProtocolJson.TryReadMessageType(json);

        try
        {
            if (type is "HELLO_NACK" or "GOING_AWAY")
            {
                HandleControlNotice(json, isNack: type == "HELLO_NACK");
                return;
            }

            if (!_helloAccepted)
            {
                // First regular message after HELLO = the backend accepted this agent.
                _helloAccepted = true;
                _retryPolicy.OnConnected();
            }

            switch (type)
            {
                case ProtocolMessageTypes.Command:
                    HandleCommandMessage(json);
                    break;

                case ProtocolMessageTypes.Ping:
                    HandlePing();
                    break;

                case "HELLO_ACK":
                    // The CloudOrc backend confirms an accepted HELLO (retry counters were reset above).
                    logger.LogInformation("Backend accepted HELLO.");
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

    private static readonly string[] SupportedCommandTypes = [CommandTypes.PowerShellExec, CommandTypes.RdpTransferPolicyApply];

    private void HandleCommandMessage(string json)
    {
        // Typed commands (backup.*, restore.*, agent.*) carry their own parameter shapes and
        // may run for hours - they go to the typed router, never the sequential script queue.
        var commandType = TryReadCommandType(json);
        if (_typedRouter is not null && _typedRouter.TryRoute(json, commandType))
        {
            return;
        }

        var message = JsonSerializer.Deserialize<CommandMessage>(json, ProtocolJson.Options);
        if (message?.Parameters is null)
        {
            logger.LogWarning("Received a COMMAND message with no parameters payload; ignoring it.");
            return;
        }

        var supported = SupportedCommandTypes.Contains(message.CommandType, StringComparer.Ordinal)
                        && (_powerShellExecEnabled || !string.Equals(message.CommandType, CommandTypes.PowerShellExec, StringComparison.Ordinal));
        if (!supported)
        {
            logger.LogWarning(
                "Rejected incoming command {CommandId}: unsupported commandType '{CommandType}'.",
                message.CommandId, message.CommandType);

            var typeError = new ErrorMessage
            {
                Message = $"Unsupported commandType '{message.CommandType}'. Supported types: {string.Join(", ", SupportedCommandTypes)}.",
                RelatedCommandId = message.CommandId,
                CorrelationId = message.CorrelationId
            };
            outgoing.TryEnqueue(JsonSerializer.Serialize(typeError, ProtocolJson.Options));
            return;
        }

        var request = new CommandRequest
        {
            CommandId = message.CommandId,
            CorrelationId = message.CorrelationId,
            CommandType = message.CommandType,
            Script = message.Parameters.Script,
            TimeoutSeconds = message.Parameters.TimeoutSeconds,
            ClientToServerClipboard = message.Parameters.ClientToServerClipboard,
            ServerToClientClipboard = message.Parameters.ServerToClientClipboard,
            DriveRedirection = message.Parameters.DriveRedirection,
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
            outgoing.TryEnqueue(JsonSerializer.Serialize(error, ProtocolJson.Options));
        }
    }

    private static string? TryReadCommandType(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("commandType", out var element) && element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
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

        outgoing.TryEnqueue(JsonSerializer.Serialize(heartbeat, ProtocolJson.Options));
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
