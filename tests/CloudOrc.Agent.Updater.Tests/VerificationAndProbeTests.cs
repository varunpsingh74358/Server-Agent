using System.Text.Json;
using CloudOrc.Agent.Updater;

namespace CloudOrc.Agent.Updater.Tests;

public sealed class VerificationAndProbeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "co-upd-verify-" + Guid.NewGuid().ToString("N"));

    public VerificationAndProbeTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Hash_match_is_case_insensitive()
    {
        var file = Path.Combine(_dir, "setup.exe");
        File.WriteAllText(file, "installer-bytes");
        var hash = FileHashVerifier.ComputeSha256Hex(file);

        Assert.True(new FileHashVerifier().Verify(file, hash.ToUpperInvariant()).IsValid);
    }

    [Fact]
    public void Hash_mismatch_fails_closed()
    {
        var file = Path.Combine(_dir, "setup.exe");
        File.WriteAllText(file, "tampered");

        var result = new FileHashVerifier().Verify(file, new string('0', 64));

        Assert.False(result.IsValid);
        Assert.Contains("mismatch", result.Reason);
    }

    [Fact]
    public void Missing_installer_fails_hash_verification()
    {
        Assert.False(new FileHashVerifier().Verify(Path.Combine(_dir, "nope.exe"), new string('0', 64)).IsValid);
    }

    [Fact]
    public void Unsigned_file_is_rejected_by_authenticode()
    {
        var file = Path.Combine(_dir, "unsigned.exe");
        File.WriteAllBytes(file, [0x4D, 0x5A, 0x00, 0x01, 0x02]);

        var result = new AuthenticodeVerifier().Verify(file);

        Assert.False(result.IsValid);
        Assert.NotNull(result.Reason);
    }

    [Theory]
    [InlineData("CloudOrc ControlAgent\r\nVersion: 1.4.0\r\n", "1.4.0")]
    [InlineData("CloudOrc ControlAgent\nVersion:2.0.0-beta.1\n", "2.0.0-beta.1")]
    [InlineData("garbage", null)]
    public void Version_line_is_parsed(string output, string? expected)
    {
        Assert.Equal(expected, ControlAgentVersionProbe.ParseVersion(output));
    }

    [Theory]
    [InlineData("{\"status\":\"HEALTHY\",\"processedCount\":3}", "HEALTHY")]
    [InlineData("{\"status\":\"DEGRADED\"}", "DEGRADED")]
    [InlineData("{}", null)]
    [InlineData("not json", null)]
    public void Health_status_is_parsed(string json, string? expected)
    {
        Assert.Equal(expected, PipeHealthProbe.ParseStatus(json));
    }

    [Fact]
    public void Result_writer_writes_camelCase_json_atomically()
    {
        var file = Path.Combine(_dir, "sub", "result.json");
        var started = new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);
        var result = new UpdateResult
        {
            UpdateId = "u1",
            FromVersion = "1.3.0",
            ToVersion = "1.4.0",
            InstalledVersion = "1.4.0",
            Outcome = UpdateOutcomes.Succeeded,
            Reason = null,
            InstallerExitCode = 0,
            StartedAt = started,
            CompletedAt = started.AddMinutes(2)
        };

        ResultWriter.Write(file, result);

        Assert.False(File.Exists(file + ".tmp"));
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var root = doc.RootElement;
        Assert.Equal("u1", root.GetProperty("updateId").GetString());
        Assert.Equal("1.3.0", root.GetProperty("fromVersion").GetString());
        Assert.Equal("1.4.0", root.GetProperty("toVersion").GetString());
        Assert.Equal("1.4.0", root.GetProperty("installedVersion").GetString());
        Assert.Equal("SUCCEEDED", root.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("reason").ValueKind);
        Assert.Equal(0, root.GetProperty("installerExitCode").GetInt32());
        Assert.Equal(started, root.GetProperty("startedAt").GetDateTimeOffset());
        Assert.Equal(started.AddMinutes(2), root.GetProperty("completedAt").GetDateTimeOffset());
    }

    [Theory]
    [InlineData(UpdateOutcomes.Succeeded, 0)]
    [InlineData(UpdateOutcomes.VerificationFailed, 10)]
    [InlineData(UpdateOutcomes.FailedBeforeInstall, 11)]
    [InlineData(UpdateOutcomes.FailedRolledBack, 12)]
    [InlineData(UpdateOutcomes.RollbackFailed, 13)]
    public void Outcome_maps_to_exit_code(string outcome, int code)
    {
        var now = DateTimeOffset.UtcNow;
        var result = new UpdateResult { UpdateId = "u", ToVersion = "1.0.0", Outcome = outcome, StartedAt = now, CompletedAt = now };
        Assert.Equal(code, result.ToExitCode());
    }
}
