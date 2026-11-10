using System.Security.AccessControl;
using System.Security.Principal;
using CloudOrc.ControlAgent.Configuration;
using CloudOrc.ControlAgent.Services;
using CloudOrc.ControlAgent.Startup;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudOrc.ControlAgent.Tests.Startup;

/// <summary>§10/§13: local command files must never be creatable by ordinary users (they run as SYSTEM).</summary>
public sealed class DataDirectoryAclTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cloudorc-acl-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void GrantUsersWrite(string dir)
    {
        var info = new DirectoryInfo(dir);
        var acl = info.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.WriteData | FileSystemRights.AppendData, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(acl);
    }

    private static async Task<int> CountYieldedAsync(LocalFileCommandSource source)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var picked = 0;
        try
        {
            await foreach (var _ in source.GetCommandsAsync(cts.Token))
            {
                picked++;
            }
        }
        catch (OperationCanceledException)
        {
        }

        return picked;
    }

    [Fact]
    public void Harden_RemovesInheritance_AndLeavesOnlySystemAndAdministrators()
    {
        Assert.Null(DataDirectoryAcl.Harden(_root));

        var acl = new DirectoryInfo(_root).GetAccessControl();
        Assert.True(acl.AreAccessRulesProtected);
        var sids = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Select(r => ((SecurityIdentifier)r.IdentityReference).Value).Distinct().OrderBy(v => v).ToList();
        Assert.Equal(new[] { "S-1-5-18", "S-1-5-32-544" }.OrderBy(v => v), sids);
        Assert.True(DataDirectoryAcl.IsWritableOnlyByPrivileged(_root, out _));
    }

    [Fact]
    public void UsersWriteAccess_IsDetected()
    {
        DataDirectoryAcl.Harden(_root);
        GrantUsersWrite(_root);
        Assert.False(DataDirectoryAcl.IsWritableOnlyByPrivileged(_root, out var offender));
        Assert.Equal("S-1-5-32-545", offender);
    }

    [Fact]
    public async Task CommandFiles_InAUserWritableFolder_AreNeverExecuted()
    {
        var options = new ControlAgentOptions { DataDirectory = _root, PollIntervalSeconds = 1, FileStabilityMilliseconds = 0 };
        Directory.CreateDirectory(options.CommandsDirectory);
        DataDirectoryAcl.Harden(_root);
        GrantUsersWrite(options.CommandsDirectory);
        File.WriteAllText(Path.Combine(options.CommandsDirectory, "evil.json"), """{"commandId":"evil","script":"whoami"}""");

        var picked = await CountYieldedAsync(new LocalFileCommandSource(Options.Create(options), NullLogger<LocalFileCommandSource>.Instance));

        Assert.Equal(0, picked);
        Assert.True(File.Exists(Path.Combine(options.CommandsDirectory, "evil.json")), "the file must be left untouched, not executed");
    }

    [Fact]
    public async Task PowerShellDisabled_RejectsLocalScriptFiles()
    {
        var options = new ControlAgentOptions { DataDirectory = _root, PollIntervalSeconds = 1, FileStabilityMilliseconds = 0, PowerShellExecEnabled = false };
        foreach (var d in new[] { options.CommandsDirectory, options.ProcessingDirectory, options.FailedDirectory, options.CompletedDirectory, options.ResultsDirectory })
        {
            Directory.CreateDirectory(d);
        }

        DataDirectoryAcl.Harden(_root);
        File.WriteAllText(Path.Combine(options.CommandsDirectory, "ps.json"), """{"commandId":"ps1","script":"Get-Date"}""");

        var picked = await CountYieldedAsync(new LocalFileCommandSource(Options.Create(options), NullLogger<LocalFileCommandSource>.Instance));

        Assert.Equal(0, picked);
        Assert.False(File.Exists(Path.Combine(options.CommandsDirectory, "ps.json")), "rejected file is moved to failed\\");
    }
}
