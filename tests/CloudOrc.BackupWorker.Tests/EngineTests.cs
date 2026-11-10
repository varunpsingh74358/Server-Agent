using System.Security.Cryptography;
using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.BackupWorker.Engines;

namespace CloudOrc.BackupWorker.Tests;

public class KopiaParsingTests
{
    [Fact]
    public void ProgressLine_IsParsed()
    {
        var p = KopiaEngine.ParseProgress(" * 0 hashing, 1250 hashed (1.5 GB), 300 cached (500 MB), uploaded 400 MB, estimated 10 GB (20.0%) 2m left")!;

        Assert.Equal(1550, p.FilesDone);
        Assert.Equal(2_000_000_000, p.BytesRead);
        Assert.Equal(400_000_000, p.BytesUploaded);
        Assert.Equal(10_000_000_000, p.BytesTotal);
        Assert.Equal(20.0, p.Percent);
    }

    [Fact]
    public void UnrelatedLines_AreIgnored() => Assert.Null(KopiaEngine.ParseProgress("Snapshotting user@host:D:\\Data ..."));

    [Fact]
    public void SnapshotManifest_IsParsed()
    {
        const string stdout = """
            some banner text
            {"id":"a1b2c3","source":{"host":"h","userName":"u","path":"D:\\Data"},
             "rootEntry":{"name":"Data","obj":"k0011aa","summ":{"size":12345,"files":10,"numFailed":2}},
             "stats":{"totalSize":12345,"fileCount":10,"cachedFiles":7,"nonCachedFiles":3,"errorCount":2}}
            """;

        var outcome = KopiaEngine.ParseSnapshotManifest(stdout, 999)!;
        Assert.True(outcome.Ok);
        Assert.Equal("a1b2c3", outcome.SnapshotId);
        Assert.Equal("k0011aa", outcome.RootObjectId);
        Assert.Equal(10, outcome.Files);
        Assert.Equal(12345, outcome.Bytes);
        Assert.Equal(2, outcome.Errors);
        Assert.Equal(3, outcome.FilesChanged);
        Assert.Equal(7, outcome.FilesUnchanged);
        Assert.Equal(999, outcome.BytesUploaded);
    }

    [Fact]
    public void SnapshotManifest_WithoutId_IsRejected() => Assert.Null(KopiaEngine.ParseSnapshotManifest("{\"rootEntry\":{}}", null));
}

public class ResticPathTests
{
    [Theory]
    [InlineData(@"D:\Tally\Data", "/D/Tally/Data")]
    [InlineData(@"C:\", "/C")]
    [InlineData(@"E:\Folder\", "/E/Folder")]
    public void WindowsPaths_MapToResticSnapshotPaths(string input, string expected) =>
        Assert.Equal(expected, ResticEngine.ToResticPath(input));
}

public class EngineErrorClassifierTests
{
    [Theory]
    [InlineData("Fatal: wrong password or no key found", BackupReasonCodes.RepositoryPasswordInvalid)]
    [InlineData("Fatal: unable to open config file: Is there a repository at the following location?", BackupReasonCodes.RepositoryNotInitialized)]
    [InlineData("repository is already locked exclusively by PID 42", BackupReasonCodes.RepositoryLocked)]
    [InlineData("googleapi: Error 403: The user's Drive storage quota has been exceeded., storageQuotaExceeded", BackupReasonCodes.StorageFull)]
    [InlineData("googleapi: Error 403: User rate limit exceeded., userRateLimitExceeded", BackupReasonCodes.StorageThrottled)]
    [InlineData("oauth2: cannot fetch token: 400 Bad Request Response: {\"error\":\"invalid_grant\"}", BackupReasonCodes.StorageCredentialExpired)]
    [InlineData("error creating volume shadow copy: VSS_E_INSUFFICIENT_STORAGE", BackupReasonCodes.SnapshotFailed)]
    [InlineData("dial tcp: lookup example.invalid: no such host", BackupReasonCodes.StorageUnavailable)]
    [InlineData("something odd happened", BackupReasonCodes.EngineFailed)]
    public void KnownErrors_MapToReasonCodes(string line, string expected) =>
        Assert.Equal(expected, EngineErrorClassifier.Classify([line]));

    [Theory]
    [InlineData("12 B", 12L)]
    [InlineData("1.5 KB", 1500L)]
    [InlineData("2 MiB", 2L * 1024 * 1024)]
    [InlineData("3 GB", 3_000_000_000L)]
    [InlineData("nonsense", null)]
    public void Sizes(string text, long? expected) => Assert.Equal(expected, EngineErrorClassifier.ParseSize(text));

    [Fact]
    public void TimeoutIsSnapshotLifetimeExceeded()
    {
        var outcome = EngineErrorClassifier.FromRun("kopia", "snapshot", new EngineRunResult(1, "", [], TimedOut: true, Cancelled: false));
        Assert.Equal(BackupReasonCodes.SnapshotLifetimeExceeded, outcome.ReasonCode);
    }

    [Fact]
    public void GoRuntimeLimits_FollowTheBudget()
    {
        var env = EngineErrorClassifier.GoRuntimeEnvironment(new WorkerBudget { Profile = "eco", GoMaxProcs = 1, GoMemoryLimitMb = 300 });
        Assert.Equal("1", env["GOMAXPROCS"]);
        Assert.Equal("300MiB", env["GOMEMLIMIT"]);
    }
}

public sealed class EngineBinaryVerifierTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cloudorc-engines-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private string WriteEngine(string name, byte[] content, string? pinnedHash = null)
    {
        var file = name + ".exe";
        File.WriteAllBytes(Path.Combine(_dir, file), content);
        var hash = pinnedHash ?? Convert.ToHexString(SHA256.HashData(content));
        File.WriteAllText(Path.Combine(_dir, EngineBinaryVerifier.ManifestFileName),
            $$"""{"engines":[{"name":"{{name}}","version":"0.1.0","file":"{{file}}","sha256":"{{hash}}"}]}""");
        return file;
    }

    [Fact]
    public void MatchingHash_Resolves()
    {
        WriteEngine("kopia", [1, 2, 3]);
        var binary = EngineBinaryVerifier.Resolve(_dir, "kopia");
        Assert.Equal("0.1.0", binary.Version);
    }

    [Fact]
    public void TamperedBinary_FailsClosed()
    {
        WriteEngine("kopia", [1, 2, 3], pinnedHash: new string('0', 64));
        var ex = Assert.Throws<EngineBinaryException>(() => EngineBinaryVerifier.Resolve(_dir, "kopia"));
        Assert.Equal(BackupReasonCodes.EngineBinaryTampered, ex.ReasonCode);
    }

    [Fact]
    public void MissingManifestOrEngine_FailsClosed()
    {
        Assert.Equal(BackupReasonCodes.EngineBinaryMissing, Assert.Throws<EngineBinaryException>(() => EngineBinaryVerifier.Resolve(_dir, "kopia")).ReasonCode);
        WriteEngine("kopia", [1]);
        Assert.Equal(BackupReasonCodes.EngineBinaryMissing, Assert.Throws<EngineBinaryException>(() => EngineBinaryVerifier.Resolve(_dir, "restic")).ReasonCode);
    }

    [Fact]
    public void PathInManifest_CannotEscapeTheEngineFolder()
    {
        File.WriteAllText(Path.Combine(_dir, EngineBinaryVerifier.ManifestFileName),
            """{"engines":[{"name":"kopia","version":"1","file":"..\\..\\evil.exe","sha256":"00"}]}""");
        Assert.Equal(BackupReasonCodes.EngineBinaryTampered, Assert.Throws<EngineBinaryException>(() => EngineBinaryVerifier.Resolve(_dir, "kopia")).ReasonCode);
    }
}

public class EngineProcessTests
{
    [Fact]
    public async Task SecretsTravelOnlyInTheEnvironment_AndStderrIsRedacted()
    {
        var cmd = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
        var result = await EngineProcess.RunAsync(cmd, ["/c", "echo %KOPIA_PASSWORD% & echo KOPIA_PASSWORD=%KOPIA_PASSWORD% 1>&2"],
            new Dictionary<string, string?> { ["KOPIA_PASSWORD"] = "s3cretvalue" }, Path.GetTempPath(), null, null, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("s3cretvalue", result.StdoutText); // the child really received it
        Assert.DoesNotContain(result.StderrTail, l => l.Contains("s3cretvalue", StringComparison.Ordinal)); // but our captured tail is redacted
    }

    [Fact]
    public async Task Timeout_KillsTheProcess()
    {
        var cmd = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
        var result = await EngineProcess.RunAsync(cmd, ["/c", "ping -n 30 127.0.0.1 > nul"],
            new Dictionary<string, string?>(), Path.GetTempPath(), null, null, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.True(result.TimedOut);
    }
}
