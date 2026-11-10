using System.IO.Compression;
using System.Text.Json;
using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.Agent.Contracts.Protocol;
using CloudOrc.BackupWorker.Consistency;
using CloudOrc.BackupWorker.Restore;
using CloudOrc.BackupWorker.Scanning;

namespace CloudOrc.BackupWorker.Tests;

public sealed class SourceScannerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cloudorc-src-").FullName;

    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void Scan_CountsFiles_HonoursExcludes_AndWritesRelativeManifest()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Company", "001"));
        Directory.CreateDirectory(Path.Combine(_root, "temp"));
        File.WriteAllText(Path.Combine(_root, "Company", "001", "ledger.tsf"), new string('x', 100));
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "hello");
        File.WriteAllText(Path.Combine(_root, "scratch.tmp"), "skip me");
        File.WriteAllText(Path.Combine(_root, "temp", "a.txt"), "skip me too");
        var manifest = Path.Combine(_root, "..", Path.GetFileName(_root) + ".manifest.ndjson.gz");

        try
        {
            var result = SourceScanner.Scan(
                [new BackupSource { Path = _root, Excludes = ["*.tmp", "temp"] }, new BackupSource { Path = Path.Combine(_root, "missing") }],
                manifest, 1000, null, CancellationToken.None);

            Assert.Equal(2, result.Files);
            Assert.Equal(105, result.Bytes);
            Assert.Equal([Path.Combine(_root, "missing")], result.MissingSources);

            var entries = ReadManifest(manifest);
            Assert.Contains(entries, e => e.Path == "Company/001/ledger.tsf" && e.Type == "file" && e.Size == 100);
            Assert.DoesNotContain(entries, e => e.Path.EndsWith(".tmp", StringComparison.Ordinal) || e.Path.StartsWith("temp", StringComparison.Ordinal));
            Assert.All(entries, e => Assert.False(Path.IsPathRooted(e.Path), "manifest paths must be relative"));
        }
        finally
        {
            File.Delete(manifest);
        }
    }

    [Fact]
    public void Manifest_IsCapped()
    {
        for (var i = 0; i < 20; i++)
        {
            File.WriteAllText(Path.Combine(_root, $"f{i}.txt"), "x");
        }

        var manifest = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".gz");
        try
        {
            var result = SourceScanner.Scan([new BackupSource { Path = _root }], manifest, 5, null, CancellationToken.None);
            Assert.True(result.ManifestTruncated);
            Assert.Equal(5, ReadManifest(manifest).Count);
            Assert.Equal(20, result.Files); // counting is not capped
        }
        finally
        {
            File.Delete(manifest);
        }
    }

    private static List<ManifestEntry> ReadManifest(string path)
    {
        using var gz = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
        using var reader = new StreamReader(gz);
        var list = new List<ManifestEntry>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            list.Add(JsonSerializer.Deserialize<ManifestEntry>(line, ProtocolJson.Options)!);
        }

        return list;
    }
}

public class GlobMatcherTests
{
    [Theory]
    [InlineData("*.tmp", "a/b/c.tmp", true)]
    [InlineData("*.tmp", "c.tmp.bak", false)]
    [InlineData("node_modules", "web/node_modules/x/y.js", true)]
    [InlineData("logs/**", "logs/2026/11/a.log", true)]
    [InlineData("logs/**", "app/logs/a.log", false)]
    [InlineData("Cache?", "cache1/file", true)]
    public void Matches(string pattern, string path, bool expected) => Assert.Equal(expected, new GlobMatcher([pattern]).IsMatch(path));
}

public class ConsistencyPlannerTests
{
    private static readonly VolumeFacts HealthyNtfs = new("NTFS", 100, 50);
    private static readonly IReadOnlyList<BackupSource> TallySource = [new BackupSource { Path = @"D:\Tally", Kind = BackupSourceKinds.Tally }];

    [Fact]
    public void HealthyVolume_TallyClosed_IsApplicationConsistent()
    {
        var plan = ConsistencyPlanner.Plan(TallySource, new BackupConsistencyPolicy(), _ => HealthyNtfs, _ => false);
        Assert.True(plan.UseVss);
        Assert.Equal(ConsistencyLevels.ApplicationConsistent, plan.ExpectedLevel);
        Assert.Null(plan.FailReasonCode);
    }

    [Fact]
    public void TallyRunning_IsCrashConsistent_AndDegradedUnderRequireClosed()
    {
        var policy = new BackupConsistencyPolicy();
        var plan = ConsistencyPlanner.Plan(TallySource, policy, _ => HealthyNtfs, _ => true);
        Assert.Equal(ConsistencyLevels.CrashConsistent, plan.ExpectedLevel);
        Assert.True(ConsistencyPlanner.IsDegraded(plan.ExpectedLevel, policy));
    }

    [Fact]
    public void TallyRunning_WithFailPolicy_FailsBeforeStarting()
    {
        var policy = new BackupConsistencyPolicy { OnInconsistent = InconsistencyActions.Fail };
        var plan = ConsistencyPlanner.Plan(TallySource, policy, _ => HealthyNtfs, _ => true);
        Assert.Equal(BackupReasonCodes.ApplicationNotQuiesced, plan.FailReasonCode);
    }

    [Fact]
    public void NotEnoughCopyOnWriteSpace_WithVssRequired_Fails()
    {
        var plan = ConsistencyPlanner.Plan(TallySource, new BackupConsistencyPolicy { MinCopyOnWriteFreePercent = 10 }, _ => new VolumeFacts("NTFS", 100, 5), _ => false);
        Assert.Equal(BackupReasonCodes.SnapshotStorageInsufficient, plan.FailReasonCode);
    }

    [Fact]
    public void FatVolume_WhenAvailable_FallsBackToLiveReads_MarkedInconsistent()
    {
        var policy = new BackupConsistencyPolicy { VssMode = VssModes.WhenAvailable };
        var plan = ConsistencyPlanner.Plan([new BackupSource { Path = @"E:\Data" }], policy, _ => new VolumeFacts("FAT32", 100, 90), _ => false);
        Assert.False(plan.UseVss);
        Assert.Equal(ConsistencyLevels.Inconsistent, plan.ExpectedLevel);
        Assert.True(ConsistencyPlanner.IsDegraded(plan.ExpectedLevel, policy));
    }

    [Fact]
    public void SnapshotLifetime_ComesFromPolicy() =>
        Assert.Equal(TimeSpan.FromMinutes(90), ConsistencyPlanner.Plan(TallySource, new BackupConsistencyPolicy { MaxSnapshotLifetimeMinutes = 90 }, _ => HealthyNtfs, _ => false).SnapshotLifetime);
}

public sealed class RestorePathGuardTests : IDisposable
{
    private readonly string _base = Directory.CreateTempSubdirectory("cloudorc-restore-").FullName;
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);

    public void Dispose() => Directory.Delete(_base, true);

    private RestoreExecuteParameters Params(string mode, string kind = RestoreTargetKinds.Alternate, IReadOnlyList<string>? selection = null, string? target = null) => new()
    {
        RestoreJobId = "r", RecoveryPointId = "rp", RepositoryId = "repo", Engine = BackupEngineNames.Kopia, EngineSnapshotId = "abc",
        SourceRoot = Path.Combine(_base, "source"), TargetKind = kind, TargetPath = target ?? Path.Combine(_base, "target"), Mode = mode, Selection = selection ?? []
    };

    [Fact]
    public void Rename_NeverWritesIntoExistingData()
    {
        var plan = RestorePathGuard.Plan(Params(RestoreModes.Rename, RestoreTargetKinds.Original), Now);
        Assert.Equal(Path.Combine(_base, "source") + ".restored-20261109-100000", plan.TargetDirectory);
    }

    [Fact]
    public void Overwrite_TargetsTheChosenFolder_WithSelectedItems()
    {
        var plan = RestorePathGuard.Plan(Params(RestoreModes.Overwrite, selection: ["Company/001", "notes.txt"]), Now);
        Assert.Equal(Path.Combine(_base, "target"), plan.TargetDirectory);
        Assert.Equal(["Company\\001", "notes.txt"], plan.Items);
    }

    [Theory]
    [InlineData("..\\..\\Windows")]
    [InlineData("a\\..\\..\\b")]
    [InlineData("x:evil")]
    public void TraversalIsRejected(string item) =>
        Assert.Throws<RestoreRejectedException>(() => RestorePathGuard.Plan(Params(RestoreModes.Overwrite, selection: [item]), Now));

    [Fact]
    public void AlternateTargetInsideWindows_IsRejected() =>
        Assert.Throws<RestoreRejectedException>(() => RestorePathGuard.Plan(
            Params(RestoreModes.Overwrite, target: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp", "x")), Now));

    [Fact]
    public void IsInside_IsNotFooledByPrefixes()
    {
        Assert.True(RestorePathGuard.IsInside(@"D:\Restore\a\b", @"D:\Restore"));
        Assert.False(RestorePathGuard.IsInside(@"D:\RestoreEvil\a", @"D:\Restore"));
    }

    [Fact]
    public void Audit_CountsFilesAndBytes()
    {
        var dir = Path.Combine(_base, "audit");
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(dir, "a.txt"), "12345");
        File.WriteAllText(Path.Combine(dir, "sub", "b.txt"), "123");
        Assert.Equal((2, 8, 0), RestorePathGuard.Audit(dir));
    }
}
