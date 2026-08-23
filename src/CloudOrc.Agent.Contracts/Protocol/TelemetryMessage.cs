using System.Text.Json.Serialization;

namespace CloudOrc.Agent.Contracts.Protocol;

/// <summary>
/// Periodic near-real-time machine telemetry. "Near-real-time" means once per the
/// configured interval (a few seconds by default) - not a continuous stream. Any metric
/// that could not be reliably collected is left null rather than fabricated.
/// </summary>
public sealed class TelemetryMessage
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = ProtocolMessageTypes.Telemetry;

    [JsonPropertyName("agentId")]
    public required string AgentId { get; init; }

    [JsonPropertyName("serverId")]
    public required string ServerId { get; init; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("machine")]
    public required TelemetryMachineInfo Machine { get; init; }

    [JsonPropertyName("cpu")]
    public TelemetryCpuInfo? Cpu { get; init; }

    [JsonPropertyName("memory")]
    public TelemetryMemoryInfo? Memory { get; init; }

    [JsonPropertyName("disks")]
    public IReadOnlyList<TelemetryDiskInfo> Disks { get; init; } = [];

    [JsonPropertyName("uptimeSeconds")]
    public required long UptimeSeconds { get; init; }
}

public sealed class TelemetryMachineInfo
{
    [JsonPropertyName("machineName")]
    public required string MachineName { get; init; }

    [JsonPropertyName("os")]
    public string? Os { get; init; }
}

public sealed class TelemetryCpuInfo
{
    [JsonPropertyName("usagePercent")]
    public double? UsagePercent { get; init; }
}

public sealed class TelemetryMemoryInfo
{
    [JsonPropertyName("totalBytes")]
    public required long TotalBytes { get; init; }

    [JsonPropertyName("usedBytes")]
    public required long UsedBytes { get; init; }

    [JsonPropertyName("availableBytes")]
    public required long AvailableBytes { get; init; }
}

public sealed class TelemetryDiskInfo
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("totalBytes")]
    public required long TotalBytes { get; init; }

    [JsonPropertyName("usedBytes")]
    public required long UsedBytes { get; init; }

    [JsonPropertyName("freeBytes")]
    public required long FreeBytes { get; init; }
}
