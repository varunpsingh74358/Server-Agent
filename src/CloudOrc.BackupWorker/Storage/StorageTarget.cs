using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.BackupWorker.Engines;

namespace CloudOrc.BackupWorker.Storage;

/// <summary>Everything an engine needs to reach the repository for one invocation.</summary>
public sealed class EngineStorage
{
    /// <summary>Environment for the engine (and the rclone it spawns). Contains SECRETS.</summary>
    public required Dictionary<string, string?> Environment { get; init; }

    /// <summary>Kopia: "repository connect|create" storage sub-command + flags (no secrets).</summary>
    public required IReadOnlyList<string> KopiaStorageArgs { get; init; }

    /// <summary>Restic: repository string for -r (no secrets).</summary>
    public required string ResticRepository { get; init; }

    /// <summary>Restic: extra -o options (no secrets).</summary>
    public IReadOnlyList<string> ResticOptions { get; init; } = [];
}

/// <summary>
/// Provider adapter on the agent side (§9 "Storage provider adapter architecture"). Turns a
/// short-lived grant into engine configuration. Adding Wasabi/B2/S3/own-DC later is only an S3
/// grant from the backend - no worker change - because all of them are S3-compatible.
/// Customer Google Drive goes through rclone (§9 "Customer Google Drive - separate existing
/// customer-controlled destination") with the loopback token broker.
/// </summary>
public static class StorageTarget
{
    public const string RcloneRemoteName = "codrive";

    public static bool NeedsRclone(StorageGrant grant) => grant.Provider == StorageProviderTypes.GoogleDrive;

    public static EngineStorage Build(StorageGrant grant, string workDirectory, EngineBinary? rclone, GoogleDriveTokenBroker? broker)
    {
        var repoPath = NormalizeRepoPath(grant.Repository.Path);
        switch (grant.Provider)
        {
            case StorageProviderTypes.GoogleDrive:
            {
                var drive = grant.GoogleDrive ?? throw new InvalidOperationException("Grant has no Google Drive section.");
                if (rclone is null || broker is null)
                {
                    throw new InvalidOperationException("Google Drive needs rclone and the token broker.");
                }

                var prefix = $"RCLONE_CONFIG_{RcloneRemoteName.ToUpperInvariant()}_";
                var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    // Isolated, empty rclone config file per job: nothing is written to the system profile.
                    ["RCLONE_CONFIG"] = Path.Combine(workDirectory, "rclone.conf"),
                    [prefix + "TYPE"] = "drive",
                    [prefix + "SCOPE"] = string.IsNullOrWhiteSpace(drive.Scope) ? "drive.file" : drive.Scope,
                    [prefix + "CLIENT_ID"] = GoogleDriveTokenBroker.ClientId,
                    [prefix + "CLIENT_SECRET"] = broker.ClientSecret,
                    [prefix + "TOKEN_URL"] = broker.TokenUrl,
                    [prefix + "TOKEN"] = GoogleDriveTokenBroker.TokenJson(drive),
                    // Google's undocumented ~750 GiB/day upload cap: fail fast instead of retrying forever (§9 caution).
                    ["RCLONE_DRIVE_STOP_ON_UPLOAD_LIMIT"] = "true"
                };
                if (!string.IsNullOrWhiteSpace(drive.RootFolderId))
                {
                    env[prefix + "ROOT_FOLDER_ID"] = drive.RootFolderId;
                }

                File.WriteAllText(Path.Combine(workDirectory, "rclone.conf"), string.Empty);
                var remote = $"{RcloneRemoteName}:{repoPath}";
                return new EngineStorage
                {
                    Environment = env,
                    KopiaStorageArgs = ["rclone", $"--remote-path={remote}", $"--rclone-exe={rclone.Path}"],
                    ResticRepository = $"rclone:{remote}",
                    ResticOptions = ["-o", $"rclone.program={rclone.Path}"]
                };
            }

            case StorageProviderTypes.S3:
            {
                var s3 = grant.S3 ?? throw new InvalidOperationException("Grant has no S3 section.");
                var endpoint = new Uri(s3.Endpoint.Contains("://", StringComparison.Ordinal) ? s3.Endpoint : "https://" + s3.Endpoint);
                var keyPrefix = string.Join('/', new[] { s3.Prefix?.Trim('/'), repoPath }.Where(p => !string.IsNullOrEmpty(p))) + "/";

                var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["AWS_ACCESS_KEY_ID"] = s3.AccessKeyId,
                    ["AWS_SECRET_ACCESS_KEY"] = s3.SecretAccessKey,
                    ["AWS_SESSION_TOKEN"] = s3.SessionToken,
                    ["AWS_DEFAULT_REGION"] = s3.Region
                };

                var kopia = new List<string> { "s3", $"--bucket={s3.Bucket}", $"--endpoint={endpoint.Authority}", $"--prefix={keyPrefix}" };
                if (!string.IsNullOrWhiteSpace(s3.Region))
                {
                    kopia.Add($"--region={s3.Region}");
                }

                if (endpoint.Scheme == Uri.UriSchemeHttp)
                {
                    kopia.Add("--disable-tls");
                }

                return new EngineStorage
                {
                    Environment = env,
                    KopiaStorageArgs = kopia,
                    ResticRepository = $"s3:{endpoint.Scheme}://{endpoint.Authority}/{s3.Bucket}/{keyPrefix.TrimEnd('/')}"
                };
            }

            default:
                throw new InvalidOperationException($"Unsupported storage provider '{grant.Provider}'.");
        }
    }

    /// <summary>Repository path inside the provider: forward slashes, no leading/trailing slash, no "..".</summary>
    public static string NormalizeRepoPath(string path)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => p is ".." or "."))
        {
            throw new InvalidOperationException("Repository path must not contain relative segments.");
        }

        return string.Join('/', parts);
    }
}
