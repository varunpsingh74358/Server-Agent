using CloudOrc.ControlAgent.Configuration;
using Microsoft.Extensions.Options;

namespace CloudOrc.ControlAgent.Backup;

/// <summary>
/// Install/data layout for the backup capability (§14 "agent/Capabilities/Backup"):
/// <code>
/// {app}\ControlAgent\            Agent Core (this process)
/// {app}\BackupWorker\            isolated worker (CloudOrc.BackupWorker.exe)
/// {app}\Engines\                 kopia.exe / restic.exe / rclone.exe + engines.manifest.json
/// {app}\Updater\                 signed self-updater
/// {data}\state\agent-state.db    durable queue (ACL: SYSTEM + Administrators)
/// {data}\backup\cache\           bounded engine caches
/// {data}\backup\work\{jobId}\    per-job scratch (deleted after the job)
/// {data}\backup\manifests\       manifests awaiting upload
/// {data}\updates\                downloaded installers + updater results
/// </code>
/// </summary>
public sealed class BackupPaths
{
    public BackupPaths(IOptions<ControlAgentOptions> controlOptions, IOptions<BackupOptions> backupOptions)
    {
        var data = controlOptions.Value.DataDirectory;
        var appRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));

        InstallRoot = appRoot;
        WorkerExecutable = backupOptions.Value.WorkerExecutablePath
                           ?? Path.Combine(appRoot, "BackupWorker", "CloudOrc.BackupWorker.exe");
        EngineDirectory = backupOptions.Value.EngineDirectory ?? Path.Combine(appRoot, "Engines");
        UpdaterDirectory = Path.Combine(appRoot, "Updater");
        StateDatabase = Path.Combine(data, "state", "agent-state.db");
        CacheRoot = Path.Combine(data, "backup", "cache");
        WorkRoot = Path.Combine(data, "backup", "work");
        ManifestRoot = Path.Combine(data, "backup", "manifests");
        UpdatesRoot = Path.Combine(data, "updates");
        InstallationIdFile = Path.Combine(data, "installation-id.txt");
    }

    public string InstallRoot { get; }
    public string WorkerExecutable { get; }
    public string EngineDirectory { get; }
    public string UpdaterDirectory { get; }
    public string StateDatabase { get; }
    public string CacheRoot { get; }
    public string WorkRoot { get; }
    public string ManifestRoot { get; }
    public string UpdatesRoot { get; }
    public string InstallationIdFile { get; }

    public string CacheFor(string repositoryId) => Path.Combine(CacheRoot, SafeName(repositoryId));

    public string WorkFor(string jobId) => Path.Combine(WorkRoot, SafeName(jobId));

    public string ManifestFor(string jobId) => Path.Combine(ManifestRoot, SafeName(jobId) + ".ndjson.gz");

    public static string SafeName(string value)
    {
        var chars = value.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray();
        var name = new string(chars).Trim('.');
        return name.Length == 0 ? "_" : name.Length > 100 ? name[..100] : name;
    }

    /// <summary>Random id created once per install (§4 duplicate/reimage handling). Never a secret.</summary>
    public static string GetOrCreateInstallationId(string file)
    {
        try
        {
            if (File.Exists(file))
            {
                var existing = File.ReadAllText(file).Trim();
                if (Guid.TryParse(existing, out _))
                {
                    return existing;
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var id = Guid.NewGuid().ToString("D");
            File.WriteAllText(file, id);
            return id;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Guid.NewGuid().ToString("D");
        }
    }

    public static long DirectorySize(string path)
    {
        try
        {
            return Directory.Exists(path)
                ? new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).Sum(f => f.Length)
                : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
