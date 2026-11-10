using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.Agent.Contracts.Versioning;
using CloudOrc.ControlAgent.Backup;
using CloudOrc.ControlAgent.Commands;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudOrc.ControlAgent.Updates;

/// <summary>"Update" section. AllowUnsigned is for LOCAL TESTING ONLY.</summary>
public sealed class UpdateOptions
{
    public const string SectionName = "Update";

    public bool Enabled { get; set; } = true;

    public long MaxPackageBytes { get; set; } = 300L * 1024 * 1024;

    public int HealthTimeoutSeconds { get; set; } = 180;

    /// <summary>Skip Authenticode (hash is still enforced). Never enable in production.</summary>
    public bool AllowUnsigned { get; set; }
}

/// <summary>
/// agent.update (§13 "signed MSI/EXE installer ... signed updater ... Staged updates by cohort with
/// signature/hash verification, health-check and rollback"). Cohorts are chosen server-side; this
/// handler downloads over HTTPS, enforces size + SHA-256 (fail closed), then hands over to the
/// separately-built signed updater (which re-verifies hash AND Authenticode, backs up, installs,
/// health-checks and rolls back). The updater runs from a copy outside {app} so the installer
/// can replace every file, and is not in any kill-on-close Job Object so it survives our stop.
/// </summary>
public sealed partial class AgentUpdateHandler(
    BackupPaths paths,
    IOptions<UpdateOptions> options,
    ILogger<AgentUpdateHandler> logger,
    IServiceProvider? services = null) : ITypedCommandHandler
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public string CommandType => BackupCommandTypes.AgentUpdate;

    public async Task<TypedCommandAcceptance> HandleAsync(TypedCommandContext context, CancellationToken cancellationToken)
    {
        var p = context.Parameters<AgentUpdateParameters>();
        var error = Validate(p);
        if (error is not null)
        {
            return TypedCommandDispatcher.Rejected("UPDATE_INVALID", error);
        }

        if (!options.Value.Enabled)
        {
            return TypedCommandDispatcher.Rejected("UPDATE_DISABLED", "Self-update is disabled on this endpoint.");
        }

        var current = AgentVersionInfo.GetVersion(typeof(AgentUpdateHandler).Assembly);
        if (string.Equals(current, p!.Version, StringComparison.OrdinalIgnoreCase))
        {
            return new TypedCommandAcceptance { Accepted = true, Message = $"Already on version {current}." };
        }

        // Never cut a running backup/restore short: the installer stops the service. The backend's
        // staged rollout simply re-sends the update later (§13 "keep Agent Core responsive").
        if (services?.GetService(typeof(BackupJobCoordinator)) is BackupJobCoordinator { RunningJobId: { } runningJob })
        {
            return TypedCommandDispatcher.Rejected("UPDATE_DEFERRED_JOB_RUNNING", $"Job {runningJob} is running; retry the update after it finishes.");
        }

        if (!Directory.Exists(paths.UpdaterDirectory) || !File.Exists(Path.Combine(paths.UpdaterDirectory, "CloudOrc.Agent.Updater.exe")))
        {
            return TypedCommandDispatcher.Rejected("UPDATER_MISSING", "The signed updater is not installed on this endpoint.");
        }

        if (!await Gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return TypedCommandDispatcher.Rejected("UPDATE_IN_PROGRESS", "An update is already in progress.");
        }

        try
        {
            var updateDir = Path.Combine(paths.UpdatesRoot, BackupPaths.SafeName(p.UpdateId!));
            Directory.CreateDirectory(updateDir);
            var installer = Path.Combine(updateDir, "CloudOrcAgentSetup.exe");

            var downloadError = await DownloadAndVerifyAsync(new Uri(p.PackageUrl!), installer, p.Sha256!, p.SizeBytes, cancellationToken).ConfigureAwait(false);
            if (downloadError is not null)
            {
                TryDelete(installer);
                return TypedCommandDispatcher.Rejected("UPDATE_VERIFICATION_FAILED", downloadError);
            }

            var updaterCopy = Path.Combine(updateDir, "updater");
            CopyDirectory(paths.UpdaterDirectory, updaterCopy);

            var start = new ProcessStartInfo(Path.Combine(updaterCopy, "CloudOrc.Agent.Updater.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = updaterCopy
            };
            foreach (var arg in new[]
                     {
                         "--installer", installer, "--sha256", p.Sha256!, "--expected-version", p.Version!,
                         "--install-dir", paths.InstallRoot, "--work-dir", paths.UpdatesRoot,
                         "--result-file", Path.Combine(paths.UpdatesRoot, $"result-{BackupPaths.SafeName(p.UpdateId!)}.json"),
                         "--update-id", p.UpdateId!, "--health-timeout-seconds", options.Value.HealthTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
                     })
            {
                start.ArgumentList.Add(arg);
            }

            if (options.Value.AllowUnsigned)
            {
                start.ArgumentList.Add("--allow-unsigned");
                logger.LogWarning("Update {UpdateId}: Authenticode verification DISABLED by configuration (testing only).", p.UpdateId);
            }

            Process.Start(start)?.Dispose();
            logger.LogInformation("Update {UpdateId} to {Version} (cohort {Cohort}) handed to the signed updater.", p.UpdateId, p.Version, p.Cohort ?? "-");
            return new TypedCommandAcceptance { Accepted = true, Message = $"Update to {p.Version} started." };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or HttpRequestException)
        {
            logger.LogError(ex, "Update {UpdateId} could not be started.", p.UpdateId);
            return TypedCommandDispatcher.Rejected("UPDATE_FAILED", "The update could not be started on this endpoint.");
        }
        finally
        {
            Gate.Release();
        }
    }

    public static string? Validate(AgentUpdateParameters? p)
    {
        if (p is null || !BackupPolicyValidator.IsSafeId(p.UpdateId))
        {
            return "updateId is required.";
        }

        if (string.IsNullOrWhiteSpace(p.Version) || !VersionPattern().IsMatch(p.Version))
        {
            return "version must look like X.Y.Z.";
        }

        if (!Uri.TryCreate(p.PackageUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
        {
            return "packageUrl must be an absolute https URL.";
        }

        if (string.IsNullOrWhiteSpace(p.Sha256) || !Sha256Pattern().IsMatch(p.Sha256))
        {
            return "sha256 must be 64 hex characters.";
        }

        return p.SizeBytes is <= 0 ? "sizeBytes must be positive." : null;
    }

    private async Task<string?> DownloadAndVerifyAsync(Uri url, string target, string expectedSha256, long? expectedSize, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return $"Download failed (HTTP {(int)response.StatusCode}).";
        }

        var max = options.Value.MaxPackageBytes;
        if (response.Content.Headers.ContentLength is { } length && length > max)
        {
            return "Package exceeds the maximum allowed size.";
        }

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > max)
                {
                    return "Package exceeds the maximum allowed size.";
                }

                sha.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }

        if (expectedSize is { } size && size != total)
        {
            return "Downloaded size does not match the announced size.";
        }

        var actual = Convert.ToHexString(sha.GetHashAndReset());
        return string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase) ? null : "SHA-256 mismatch - package rejected.";
    }

    private static void CopyDirectory(string source, string destination)
    {
        if (Directory.Exists(destination))
        {
            Directory.Delete(destination, recursive: true);
        }

        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
        }

        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: true);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [GeneratedRegex(@"^\d+\.\d+\.\d+([\-+][0-9A-Za-z.\-]+)?$")]
    private static partial Regex VersionPattern();

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Pattern();
}
