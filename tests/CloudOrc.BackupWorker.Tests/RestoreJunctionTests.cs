using System.Diagnostics;
using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.BackupWorker.Restore;

namespace CloudOrc.BackupWorker.Tests;

/// <summary>A junction planted INSIDE an existing restore target must stop overwrite/skip-existing restores.</summary>
public sealed class RestoreJunctionTests : IDisposable
{
    private readonly string _base = Directory.CreateTempSubdirectory("cloudorc-junction-").FullName;

    public void Dispose()
    {
        var link = Path.Combine(_base, "source", "Company", "evil");
        if (Directory.Exists(link))
        {
            Directory.Delete(link); // removes the junction itself, never its target
        }

        Directory.Delete(_base, true);
    }

    private void PlantJunction()
    {
        var inside = Path.Combine(_base, "source", "Company");
        Directory.CreateDirectory(inside);
        var cmd = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
        using var p = Process.Start(new ProcessStartInfo(cmd, $"/c mklink /J \"{Path.Combine(inside, "evil")}\" \"{Path.GetTempPath()}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
        })!;
        p.WaitForExit();
        Assert.True(Directory.Exists(Path.Combine(inside, "evil")), "test setup: junction not created");
    }

    private RestoreExecuteParameters Params(string mode) => new()
    {
        RestoreJobId = "r", RecoveryPointId = "rp", RepositoryId = "repo", Engine = BackupEngineNames.Kopia, EngineSnapshotId = "abc",
        SourceRoot = Path.Combine(_base, "source"), TargetKind = RestoreTargetKinds.Original, Mode = mode
    };

    [Theory]
    [InlineData(RestoreModes.Overwrite)]
    [InlineData(RestoreModes.SkipExisting)]
    public void ExistingJunctionInsideTarget_IsRefused(string mode)
    {
        PlantJunction();
        var ex = Assert.Throws<RestoreRejectedException>(() => RestorePathGuard.Plan(Params(mode), DateTimeOffset.UtcNow));
        Assert.Contains("junction", ex.Message);
    }

    [Fact]
    public void RenameRestore_GoesToANewFolder_SoItIsStillAllowed()
    {
        PlantJunction();
        var plan = RestorePathGuard.Plan(Params(RestoreModes.Rename), DateTimeOffset.UtcNow);
        Assert.Contains(".restored-", plan.TargetDirectory);
    }
}
