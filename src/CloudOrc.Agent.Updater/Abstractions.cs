namespace CloudOrc.Agent.Updater;

/// <summary>Outcome of a verification step: success, or a reason that is safe to log.</summary>
public sealed record VerificationResult(bool IsValid, string? Reason)
{
    public static VerificationResult Valid() => new(true, null);
    public static VerificationResult Invalid(string reason) => new(false, reason);
}

/// <summary>Authenticode signature check of the installer (fail closed).</summary>
public interface IAuthenticodeVerifier
{
    VerificationResult Verify(string filePath);
}

/// <summary>SHA-256 check of the installer against the value the backend signed into the command.</summary>
public interface IFileHashVerifier
{
    VerificationResult Verify(string filePath, string expectedSha256Hex);
}

/// <summary>Start/stop/query the two Windows services the installer manages.</summary>
public interface IServiceControl
{
    bool IsRunning(string serviceName);

    /// <summary>Stops the service if present and not already stopped. Returns false on timeout/failure.</summary>
    bool Stop(string serviceName, TimeSpan timeout);

    /// <summary>Starts the service if present and not running. Returns false on timeout/failure.</summary>
    bool Start(string serviceName, TimeSpan timeout);
}

/// <summary>Reads the Control Agent's health status ("HEALTHY"/"DEGRADED") from its local health pipe.</summary>
public interface IHealthProbe
{
    Task<string?> GetStatusAsync(CancellationToken cancellationToken);
}

/// <summary>Reads the installed Control Agent version via its <c>--version</c> switch.</summary>
public interface IVersionProbe
{
    Task<string?> GetInstalledVersionAsync(CancellationToken cancellationToken);
}

/// <summary>Runs the Inno Setup installer silently and returns its exit code.</summary>
public interface IInstallerRunner
{
    /// <summary>Returns the exit code, or null if it timed out (and was killed) or could not be started.</summary>
    Task<int?> RunAsync(string installerPath, string installerLogPath, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>Whole-directory backup and exact restore (including deletion of files that did not exist before).</summary>
public interface IDirectoryBackup
{
    void Backup(string sourceDirectory, string backupDirectory);

    void Restore(string backupDirectory, string targetDirectory);

    void DeleteBackup(string backupDirectory);
}

public interface IUpdateLog
{
    void Info(string message);
    void Warn(string message);
    void Error(string message);
}
