using System.Text.Json;
using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.Agent.Contracts.Commands;
using CloudOrc.Agent.Contracts.Protocol;
using CloudOrc.ControlAgent.Backend;
using CloudOrc.ControlAgent.DurableQueue;
using CloudOrc.ControlAgent.Transport;
using Microsoft.Extensions.Logging;

namespace CloudOrc.ControlAgent.Commands;

/// <summary>The common envelope fields of every typed command (parameters parsed by the handler).</summary>
public sealed record TypedCommandContext(string CommandId, string? CorrelationId, string CommandType, string CommandVersion, string? Actor, string RawJson)
{
    /// <summary>
    /// The command parameters. When the envelope carries the signed "parametersJson" text (backend
    /// v2 dispatch) that exact text is used, so what executes is exactly what was signature-checked.
    /// </summary>
    public T? Parameters<T>() where T : class
    {
        using (var document = JsonDocument.Parse(RawJson))
        {
            if (document.RootElement.TryGetProperty("parametersJson", out var signed) && signed.ValueKind == JsonValueKind.String)
            {
                return JsonSerializer.Deserialize<T>(signed.GetString()!, ProtocolJson.Options);
            }
        }

        return JsonSerializer.Deserialize<TypedCommandMessage<T>>(RawJson, ProtocolJson.Options)?.Parameters;
    }
}

public interface ITypedCommandHandler
{
    string CommandType { get; }

    Task<TypedCommandAcceptance> HandleAsync(TypedCommandContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Entry point for every typed command (§10: "typed, ... versioned, allow-listed, idempotent and
/// audited; no arbitrary shell"). For each COMMAND it:
/// 1. checks the type is allow-listed and the major command version is understood;
/// 2. records it in the durable inbox FIRST - a redelivered commandId replays the stored
///    COMMAND_RESULT instead of executing twice (§3 "idempotent ACK/result", §15);
/// 3. runs the handler, which only ACCEPTS work (creates a durable job, stores a policy...).
///    Long-running progress is reported through job events, not through this result.
/// The COMMAND_RESULT output[0] is a JSON <see cref="TypedCommandAcceptance"/>.
/// </summary>
public sealed class TypedCommandDispatcher : ITypedCommandRouter
{
    private readonly IReadOnlyDictionary<string, ITypedCommandHandler> _handlers;
    private readonly CommandInboxStore _inbox;
    private readonly OutgoingMessageChannel _outgoing;
    private readonly TimeProvider _time;
    private readonly ILogger<TypedCommandDispatcher> _logger;
    private readonly CommandSignatureVerifier? _signatures;

    public TypedCommandDispatcher(
        IEnumerable<ITypedCommandHandler> handlers,
        CommandInboxStore inbox,
        OutgoingMessageChannel outgoing,
        TimeProvider time,
        ILogger<TypedCommandDispatcher> logger,
        CommandSignatureVerifier? signatures = null)
    {
        _signatures = signatures;
        _handlers = handlers.ToDictionary(h => h.CommandType, StringComparer.Ordinal);
        _inbox = inbox;
        _outgoing = outgoing;
        _time = time;
        _logger = logger;
    }

    public bool TryRoute(string json, string? commandType)
    {
        if (!BackupCommandTypes.IsTyped(commandType))
        {
            return false;
        }

        // Off the WebSocket receive loop: handlers touch SQLite and may call the control plane.
        _ = Task.Run(() => DispatchAsync(json, commandType!));
        return true;
    }

    public async Task DispatchAsync(string json, string commandType)
    {
        TypedCommandContext context;
        try
        {
            context = ParseEnvelope(json, commandType);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            _logger.LogWarning("Rejected a malformed {CommandType} command: {Error}", commandType, ex.Message);
            Send(new ErrorMessage { Message = $"Malformed {commandType} command: {ex.Message}" });
            return;
        }

        var startedAt = _time.GetUtcNow();

        // §10 signed commands: verified BEFORE the inbox, so a forged command never occupies a commandId.
        if (_signatures is not null)
        {
            SignatureCheck check;
            try
            {
                check = _signatures.Verify(json);
            }
            catch (JsonException)
            {
                check = new SignatureCheck(false, "SIGNATURE_INVALID", "Unreadable command.");
            }

            if (!check.Ok)
            {
                _logger.LogWarning("Rejected {CommandType} {CommandId}: {Reason}", context.CommandType, context.CommandId, check.Message);
                SendResult(context, startedAt, Rejected(check.ReasonCode!, check.Message!));
                return;
            }
        }

        InboxEntry entry;
        try
        {
            entry = _inbox.Begin(context.CommandId, context.CommandType, context.CorrelationId, context.Actor, startedAt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not record command {CommandId} in the durable inbox; refusing it.", context.CommandId);
            SendResult(context, startedAt, Rejected("LOCAL_STATE_UNAVAILABLE", "Agent durable state is unavailable."));
            return;
        }

        switch (entry.Disposition)
        {
            case InboxDisposition.Completed when entry.ResultJson is not null:
                _logger.LogInformation("Command {CommandId} was already handled; replaying its result.", context.CommandId);
                _outgoing.TryEnqueue(entry.ResultJson);
                return;

            case InboxDisposition.InProgress:
                Send(new CommandStatusMessage { CommandId = context.CommandId, CorrelationId = context.CorrelationId, Status = CommandStatus.Running });
                return;
        }

        Send(new CommandStatusMessage { CommandId = context.CommandId, CorrelationId = context.CorrelationId, Status = CommandStatus.Running });

        TypedCommandAcceptance acceptance;
        if (!IsSupportedVersion(context.CommandVersion))
        {
            acceptance = Rejected("UNSUPPORTED_COMMAND_VERSION", $"commandVersion '{context.CommandVersion}' is not supported (expected 1.x).");
        }
        else if (!_handlers.TryGetValue(context.CommandType, out var handler))
        {
            acceptance = Rejected("CAPABILITY_NOT_AVAILABLE", $"'{context.CommandType}' is not available on this agent build/configuration.");
        }
        else
        {
            try
            {
                acceptance = await handler.HandleAsync(context, CancellationToken.None).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                acceptance = Rejected(BackupReasonCodes.PolicyInvalid, $"Invalid parameters: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Handler for {CommandType} failed on command {CommandId}.", context.CommandType, context.CommandId);
                acceptance = Rejected("AGENT_ERROR", "The agent could not process the command.");
            }
        }

        var resultJson = SendResult(context, startedAt, acceptance);
        try
        {
            _inbox.Complete(context.CommandId, resultJson, _time.GetUtcNow());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not persist the result of command {CommandId}.", context.CommandId);
        }

        _logger.LogInformation(
            "Typed command {CommandType} {CommandId} (actor={Actor}): accepted={Accepted} {Reason}",
            context.CommandType, context.CommandId, context.Actor ?? "-", acceptance.Accepted, acceptance.ReasonCode ?? "");
    }

    public static TypedCommandAcceptance Rejected(string reasonCode, string message) =>
        new() { Accepted = false, ReasonCode = reasonCode, Message = message };

    private string SendResult(TypedCommandContext context, DateTimeOffset startedAt, TypedCommandAcceptance acceptance)
    {
        var completedAt = _time.GetUtcNow();
        var message = new CommandResultMessage
        {
            CommandId = context.CommandId,
            CorrelationId = context.CorrelationId,
            Status = acceptance.Accepted ? CommandStatus.Success : CommandStatus.Failed,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            DurationMilliseconds = (long)(completedAt - startedAt).TotalMilliseconds,
            Output = [JsonSerializer.Serialize(acceptance, ProtocolJson.Options)],
            Error = acceptance.Accepted ? null : acceptance.Message
        };

        var json = JsonSerializer.Serialize(message, ProtocolJson.Options);
        _outgoing.TryEnqueue(json);
        return json;
    }

    private void Send<T>(T message) => _outgoing.TryEnqueue(JsonSerializer.Serialize(message, ProtocolJson.Options));

    private static bool IsSupportedVersion(string version) =>
        version == "1" || version.StartsWith("1.", StringComparison.Ordinal);

    private static TypedCommandContext ParseEnvelope(string json, string commandType)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        string? Get(string name) =>
            root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String ? element.GetString() : null;

        var commandId = Get("commandId");
        if (string.IsNullOrWhiteSpace(commandId) || commandId.Length > 200 || commandId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("commandId is required (1-200 file-name-safe characters).");
        }

        return new TypedCommandContext(commandId, Get("correlationId"), commandType, Get("commandVersion") ?? BackupSchema.Version, Get("actor"), json);
    }
}
