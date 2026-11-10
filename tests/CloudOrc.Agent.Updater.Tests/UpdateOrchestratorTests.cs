using CloudOrc.Agent.Updater;

namespace CloudOrc.Agent.Updater.Tests;

public class UpdateOrchestratorTests
{
    private static UpdaterArguments Args(bool allowUnsigned = false) => new()
    {
        InstallerPath = @"C:\work\setup.exe",
        Sha256 = new string('a', 64),
        ExpectedVersion = "2.0.0",
        InstallDirectory = @"C:\install",
        WorkDirectory = @"C:\work",
        ResultFile = @"C:\work\result.json",
        UpdateId = "u1",
        HealthTimeoutSeconds = 0, // single health poll per check - keeps tests instant
        AllowUnsigned = allowUnsigned
    };

    private sealed class Harness
    {
        public VerificationResult HashResult { get; set; } = VerificationResult.Valid();
        public VerificationResult SignatureResult { get; set; } = VerificationResult.Valid();
        public int AuthenticodeCalls { get; private set; }
        public int? InstallerExitCode { get; set; } = 0;
        public int InstallerCalls { get; private set; }
        public bool BackupThrows { get; set; }
        public List<string> Events { get; } = [];

        /// <summary>Version reported by --version. Becomes the new version only once the installer "succeeds".</summary>
        public string? CurrentVersion { get; set; } = "1.0.0";
        public string? VersionAfterInstall { get; set; } = "2.0.0";
        public string? HealthStatus { get; set; } = "HEALTHY";
        public string? HealthStatusAfterRollback { get; set; } = "HEALTHY";
        public bool Restored { get; private set; }
        public bool ServicesRunning { get; set; } = true;

        public UpdateOrchestrator Build() => new(
            new FakeHash(this), new FakeSig(this), new FakeVersion(this), new FakeHealth(this), new FakeServices(this),
            new FakeInstaller(this), new FakeBackup(this), new NullLog(), TimeProvider.System,
            pollInterval: TimeSpan.Zero);

        private sealed class FakeHash(Harness h) : IFileHashVerifier
        {
            public VerificationResult Verify(string filePath, string expectedSha256Hex) => h.HashResult;
        }

        private sealed class FakeSig(Harness h) : IAuthenticodeVerifier
        {
            public VerificationResult Verify(string filePath)
            {
                h.AuthenticodeCalls++;
                return h.SignatureResult;
            }
        }

        private sealed class FakeVersion(Harness h) : IVersionProbe
        {
            public Task<string?> GetInstalledVersionAsync(CancellationToken cancellationToken) => Task.FromResult(h.CurrentVersion);
        }

        private sealed class FakeHealth(Harness h) : IHealthProbe
        {
            public Task<string?> GetStatusAsync(CancellationToken cancellationToken) =>
                Task.FromResult(h.Restored ? h.HealthStatusAfterRollback : h.HealthStatus);
        }

        private sealed class FakeServices(Harness h) : IServiceControl
        {
            public bool IsRunning(string serviceName) => h.ServicesRunning;

            public bool Stop(string serviceName, TimeSpan timeout)
            {
                h.Events.Add("stop:" + serviceName);
                return true;
            }

            public bool Start(string serviceName, TimeSpan timeout)
            {
                h.Events.Add("start:" + serviceName);
                h.ServicesRunning = true;
                return true;
            }
        }

        private sealed class FakeInstaller(Harness h) : IInstallerRunner
        {
            public Task<int?> RunAsync(string installerPath, string installerLogPath, TimeSpan timeout, CancellationToken cancellationToken)
            {
                h.InstallerCalls++;
                h.Events.Add("install");
                if (h.InstallerExitCode == 0)
                {
                    h.CurrentVersion = h.VersionAfterInstall;
                }

                return Task.FromResult(h.InstallerExitCode);
            }
        }

        private sealed class FakeBackup(Harness h) : IDirectoryBackup
        {
            private string? _savedVersion;

            public void Backup(string sourceDirectory, string backupDirectory)
            {
                if (h.BackupThrows)
                {
                    throw new IOException("disk full");
                }

                _savedVersion = h.CurrentVersion;
                h.Events.Add("backup");
            }

            public void Restore(string backupDirectory, string targetDirectory)
            {
                h.CurrentVersion = _savedVersion;
                h.Restored = true;
                h.Events.Add("restore");
            }

            public void DeleteBackup(string backupDirectory) => h.Events.Add("delete-backup");
        }

        private sealed class NullLog : IUpdateLog
        {
            public void Info(string message) { }
            public void Warn(string message) { }
            public void Error(string message) { }
        }
    }

    [Fact]
    public async Task Success_path_installs_checks_health_and_deletes_backup()
    {
        var h = new Harness();

        var result = await h.Build().RunAsync(Args(), CancellationToken.None);

        Assert.Equal(UpdateOutcomes.Succeeded, result.Outcome);
        Assert.Equal("1.0.0", result.FromVersion);
        Assert.Equal("2.0.0", result.InstalledVersion);
        Assert.Equal(0, result.InstallerExitCode);
        Assert.Equal(["backup", "install", "delete-backup"], h.Events);
        Assert.Equal(0, result.ToExitCode());
    }

    [Fact]
    public async Task Hash_failure_is_verification_failed_and_never_installs()
    {
        var h = new Harness { HashResult = VerificationResult.Invalid("SHA-256 mismatch") };

        var result = await h.Build().RunAsync(Args(), CancellationToken.None);

        Assert.Equal(UpdateOutcomes.VerificationFailed, result.Outcome);
        Assert.Equal(0, h.InstallerCalls);
        Assert.Empty(h.Events);
    }

    [Fact]
    public async Task Unsigned_installer_is_rejected_without_flag()
    {
        var h = new Harness { SignatureResult = VerificationResult.Invalid("file is not signed") };

        var result = await h.Build().RunAsync(Args(), CancellationToken.None);

        Assert.Equal(UpdateOutcomes.VerificationFailed, result.Outcome);
        Assert.Contains("not signed", result.Reason);
        Assert.Equal(0, h.InstallerCalls);
    }

    [Fact]
    public async Task Allow_unsigned_skips_authenticode_but_still_checks_hash()
    {
        var h = new Harness { SignatureResult = VerificationResult.Invalid("file is not signed") };

        var result = await h.Build().RunAsync(Args(allowUnsigned: true), CancellationToken.None);

        Assert.Equal(UpdateOutcomes.Succeeded, result.Outcome);
        Assert.Equal(0, h.AuthenticodeCalls);

        var tampered = new Harness { HashResult = VerificationResult.Invalid("mismatch") };
        var tamperedResult = await tampered.Build().RunAsync(Args(allowUnsigned: true), CancellationToken.None);
        Assert.Equal(UpdateOutcomes.VerificationFailed, tamperedResult.Outcome);
    }

    [Fact]
    public async Task Backup_failure_aborts_before_install()
    {
        var h = new Harness { BackupThrows = true };

        var result = await h.Build().RunAsync(Args(), CancellationToken.None);

        Assert.Equal(UpdateOutcomes.FailedBeforeInstall, result.Outcome);
        Assert.Equal(0, h.InstallerCalls);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    public async Task Installer_nonzero_exit_rolls_back(int exitCode)
    {
        var h = new Harness { InstallerExitCode = exitCode };

        var result = await h.Build().RunAsync(Args(), CancellationToken.None);

        Assert.Equal(UpdateOutcomes.FailedRolledBack, result.Outcome);
        Assert.Equal(exitCode, result.InstallerExitCode);
        Assert.Equal("1.0.0", result.InstalledVersion);
        Assert.Contains("restore", h.Events);
        Assert.Contains("stop:CloudOrcControlAgent", h.Events);
        Assert.Contains("start:CloudOrcWatchdogAgent", h.Events);
        Assert.DoesNotContain("delete-backup", h.Events);
        // Services are stopped before the restore and started after it.
        Assert.True(h.Events.IndexOf("stop:CloudOrcControlAgent") < h.Events.IndexOf("restore"));
        Assert.True(h.Events.IndexOf("restore") < h.Events.IndexOf("start:CloudOrcControlAgent"));
    }

    [Fact]
    public async Task Installer_timeout_rolls_back()
    {
        var h = new Harness { InstallerExitCode = null };

        var result = await h.Build().RunAsync(Args(), CancellationToken.None);

        Assert.Equal(UpdateOutcomes.FailedRolledBack, result.Outcome);
        Assert.Null(result.InstallerExitCode);
    }

    [Fact]
    public async Task Health_failure_after_install_rolls_back()
    {
        var h = new Harness { HealthStatus = "DEGRADED", HealthStatusAfterRollback = "HEALTHY" };

        var result = await h.Build().RunAsync(Args(), CancellationToken.None);

        Assert.Equal(UpdateOutcomes.FailedRolledBack, result.Outcome);
        Assert.Contains("DEGRADED", result.Reason);
        Assert.Equal("1.0.0", result.InstalledVersion);
    }

    [Fact]
    public async Task Wrong_version_after_install_rolls_back()
    {
        var h = new Harness { VersionAfterInstall = "1.9.9" };

        var result = await h.Build().RunAsync(Args(), CancellationToken.None);

        Assert.Equal(UpdateOutcomes.FailedRolledBack, result.Outcome);
        Assert.Contains("expected 2.0.0", result.Reason);
    }

    [Fact]
    public async Task Rollback_health_failure_is_rollback_failed_and_keeps_backup()
    {
        var h = new Harness { HealthStatus = "DEGRADED", HealthStatusAfterRollback = null };

        var result = await h.Build().RunAsync(Args(), CancellationToken.None);

        Assert.Equal(UpdateOutcomes.RollbackFailed, result.Outcome);
        Assert.Contains("Rollback failed", result.Reason);
        Assert.DoesNotContain("delete-backup", h.Events);
        Assert.Equal(13, result.ToExitCode());
    }
}
