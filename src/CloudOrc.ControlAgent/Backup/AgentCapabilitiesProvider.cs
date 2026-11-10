using System.Text.Json;
using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.ControlAgent.Configuration;
using CloudOrc.ControlAgent.Transport;
using Microsoft.Extensions.Options;

namespace CloudOrc.ControlAgent.Backup;

/// <summary>
/// HELLO capability profile (§3 "capability profiles"). "backup.v1" is only advertised when the
/// worker AND at least one engine are actually installed, so the backend never sends a backup
/// command this endpoint cannot execute.
/// </summary>
public sealed class AgentCapabilitiesProvider : IAgentCapabilitiesProvider
{
    public AgentCapabilitiesProvider(IOptions<BackupOptions> options, BackupPaths paths)
    {
        var caps = new List<string>
        {
            AgentCapabilities.DurableEventsV1,
            AgentCapabilities.ActivationCodeV1,
            AgentCapabilities.SessionRenewV1
        };

        if (Directory.Exists(paths.UpdaterDirectory))
        {
            caps.Add(AgentCapabilities.SignedUpdateV1);
        }

        EngineVersions = ReadEngineVersions(paths.EngineDirectory);
        if (options.Value.Enabled && File.Exists(paths.WorkerExecutable) && EngineVersions is { Count: > 0 })
        {
            caps.Add(AgentCapabilities.BackupV1);
            caps.Add(AgentCapabilities.RestoreV1);
        }

        Capabilities = caps;
    }

    public IReadOnlyList<string> Capabilities { get; }

    public IReadOnlyDictionary<string, string>? EngineVersions { get; }

    /// <summary>
    /// engines.manifest.json (shipped by the signed installer) - pins every engine binary by
    /// SHA-256 so the worker can fail closed on a tampered binary (§15).
    /// <code>{"engines":[{"name":"kopia","version":"0.21.1","file":"kopia.exe","sha256":"..."}]}</code>
    /// </summary>
    public static IReadOnlyDictionary<string, string>? ReadEngineVersions(string engineDirectory)
    {
        var manifest = Path.Combine(engineDirectory, "engines.manifest.json");
        if (!File.Exists(manifest))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var engine in doc.RootElement.GetProperty("engines").EnumerateArray())
            {
                var name = engine.GetProperty("name").GetString();
                var version = engine.TryGetProperty("version", out var v) ? v.GetString() : null;
                var file = engine.TryGetProperty("file", out var f) ? f.GetString() : null;
                if (name is BackupEngineNames.Kopia or BackupEngineNames.Restic or "rclone"
                    && version is not null && file is not null && File.Exists(Path.Combine(engineDirectory, file)))
                {
                    result[name] = version;
                }
            }

            return result.Keys.Any(k => k is BackupEngineNames.Kopia or BackupEngineNames.Restic) ? result : null;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
