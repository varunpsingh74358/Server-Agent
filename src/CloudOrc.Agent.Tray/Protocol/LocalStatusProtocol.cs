using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudOrc.Agent.Tray.Protocol;

/// <summary>
/// Local copy of the tray &lt;-&gt; Agent Core IPC constants. MUST match
/// <c>LocalStatusProtocol</c> / <c>LocalAgentStates</c> in
/// src/CloudOrc.Agent.Contracts/Backup/WorkerProtocol.cs (duplicated on purpose so the tray
/// has no dependency on the agent core assemblies).
/// </summary>
public static class LocalStatusProtocol
{
    public const string PipeName = "CloudOrc.Agent.Status";

    public const string ActionStatus = "status";
    public const string ActionActivate = "activate";
    public const string ActionCancelActivation = "cancel-activation";
    public const string ActionRetry = "retry";

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

/// <summary>The four local screens of §4 (plus OFFLINE, shown on the Connected screen).</summary>
public static class LocalAgentStates
{
    public const string NotActivated = "NOT_ACTIVATED";
    public const string Connecting = "CONNECTING";
    public const string Connected = "CONNECTED";
    public const string Offline = "OFFLINE";
    public const string Problem = "PROBLEM";
}

public sealed class LocalStatusRequest
{
    [JsonPropertyName("action")] public required string Action { get; init; }
    [JsonPropertyName("code")] public string? Code { get; init; }
}

/// <summary>
/// What Agent Core returns. By design (§4) it never carries schedule, retention, source
/// paths, keys, destinations or any backup/restore control - and this app never asks for them.
/// </summary>
public sealed class LocalStatusResponse
{
    [JsonPropertyName("ok")] public bool Ok { get; init; } = true;
    [JsonPropertyName("error")] public string? Error { get; init; }
    [JsonPropertyName("state")] public string State { get; init; } = string.Empty;
    [JsonPropertyName("deviceName")] public string DeviceName { get; init; } = string.Empty;
    [JsonPropertyName("agentVersion")] public string AgentVersion { get; init; } = string.Empty;
    [JsonPropertyName("lastContactAt")] public DateTimeOffset? LastContactAt { get; init; }
    [JsonPropertyName("retryStatus")] public string? RetryStatus { get; init; }
    [JsonPropertyName("problem")] public string? Problem { get; init; }
    [JsonPropertyName("canReconnect")] public bool CanReconnect { get; init; }
    [JsonPropertyName("diagnosticsId")] public string DiagnosticsId { get; init; } = string.Empty;
    [JsonPropertyName("protectionSummary")] public string? ProtectionSummary { get; init; }
    [JsonPropertyName("portalUrl")] public string? PortalUrl { get; init; }
}
