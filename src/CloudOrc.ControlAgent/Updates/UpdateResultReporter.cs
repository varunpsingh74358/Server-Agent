using System.Text.Json;
using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.Agent.Contracts.Identity;
using CloudOrc.ControlAgent.Backup;
using CloudOrc.ControlAgent.DurableQueue;
using CloudOrc.ControlAgent.Transport;
using Microsoft.Extensions.Logging;

namespace CloudOrc.ControlAgent.Updates;

/// <summary>
/// On startup, turns any updater result file (written by CloudOrc.Agent.Updater) into a durable
/// agent.update.result event in the outbox, so the backend's staged rollout sees SUCCEEDED /
/// FAILED_ROLLED_BACK / ROLLBACK_FAILED per endpoint and can halt a bad cohort (§13, §15).
/// </summary>
public sealed class UpdateResultReporter(
    BackupPaths paths,
    OutboxStore outbox,
    AgentIdentity identity,
    TimeProvider time,
    ILogger<UpdateResultReporter> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            ReportPending();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not report updater results.");
        }

        return Task.CompletedTask;
    }

    public int ReportPending()
    {
        if (!Directory.Exists(paths.UpdatesRoot))
        {
            return 0;
        }

        var reported = 0;
        foreach (var file in Directory.EnumerateFiles(paths.UpdatesRoot, "result-*.json"))
        {
            Dictionary<string, JsonElement>? result;
            try
            {
                result = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(file));
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                logger.LogWarning(ex, "Unreadable updater result {File}.", file);
                continue;
            }

            if (result is null)
            {
                continue;
            }

            string? Get(string key) => result.TryGetValue(key, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString() : null;

            var updateId = Get("updateId") ?? Path.GetFileNameWithoutExtension(file);
            var evt = new AgentLifecycleEvent
            {
                EventId = $"update-{updateId}",
                EventType = "agent.update.result",
                EndpointId = identity.AgentId,
                OccurredAt = time.GetUtcNow(),
                Data = new Dictionary<string, string?>
                {
                    ["update_id"] = updateId,
                    ["outcome"] = Get("outcome"),
                    ["from_version"] = Get("fromVersion"),
                    ["to_version"] = Get("toVersion"),
                    ["installed_version"] = Get("installedVersion"),
                    ["reason"] = SecretRedactor.Redact(Get("reason")),
                    ["installer_exit_code"] = Get("installerExitCode"),
                    ["completed_at"] = Get("completedAt")
                }
            };

            outbox.Enqueue(new OutboxItem
            {
                EventId = evt.EventId,
                Kind = OutboxKinds.AgentEvent,
                Route = ControlPlaneRoutes.AgentEvents,
                Payload = JsonSerializer.Serialize(evt, BackendApiClient.SnakeCaseJson),
                IsTerminal = true
            }, time.GetUtcNow());

            logger.LogInformation("Reported update {UpdateId} outcome {Outcome}.", updateId, Get("outcome"));
            try
            {
                File.Delete(file);
                var updateDir = Path.Combine(paths.UpdatesRoot, BackupPaths.SafeName(updateId));
                if (Directory.Exists(updateDir))
                {
                    Directory.Delete(updateDir, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Could not clean up update files for {UpdateId}.", updateId);
            }

            reported++;
        }

        return reported;
    }
}
