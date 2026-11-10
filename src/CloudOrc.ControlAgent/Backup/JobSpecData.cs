using System.Text.Json;
using System.Text.Json.Serialization;
using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.Agent.Contracts.Protocol;

namespace CloudOrc.ControlAgent.Backup;

/// <summary>
/// Non-secret, durable parameters of a job (stored in backup_jobs.spec_json) so a job can be
/// re-run after an agent restart exactly as it was requested. Never holds grants or passwords.
/// </summary>
public sealed class JobSpecData
{
    [JsonPropertyName("policyEffectiveHash")] public string? PolicyEffectiveHash { get; init; }
    [JsonPropertyName("resourceProfile")] public string? ResourceProfile { get; init; }
    [JsonPropertyName("reason")] public string? Reason { get; init; }
    [JsonPropertyName("actor")] public string? Actor { get; init; }
    [JsonPropertyName("verify")] public VerifyParameters? Verify { get; init; }
    [JsonPropertyName("maintenance")] public MaintenanceParameters? Maintenance { get; init; }
    [JsonPropertyName("restore")] public RestoreExecuteParameters? Restore { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, ProtocolJson.Options);

    public static JobSpecData FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new JobSpecData();
        }

        try
        {
            return JsonSerializer.Deserialize<JobSpecData>(json, ProtocolJson.Options) ?? new JobSpecData();
        }
        catch (JsonException)
        {
            return new JobSpecData();
        }
    }
}
