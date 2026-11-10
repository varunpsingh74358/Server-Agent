using CloudOrc.Agent.Tray.Protocol;

namespace CloudOrc.Agent.Tray.Tests;

/// <summary>
/// The tray duplicates the local status constants instead of referencing the contracts
/// assembly. Pin the wire values so an accidental edit is caught here.
/// </summary>
public sealed class ProtocolParityTests
{
    [Fact]
    public void WireConstantsMatchAgentCoreContract()
    {
        Assert.Equal("CloudOrc.Agent.Status", LocalStatusProtocol.PipeName);
        Assert.Equal("status", LocalStatusProtocol.ActionStatus);
        Assert.Equal("activate", LocalStatusProtocol.ActionActivate);
        Assert.Equal("cancel-activation", LocalStatusProtocol.ActionCancelActivation);
        Assert.Equal("retry", LocalStatusProtocol.ActionRetry);

        Assert.Equal("NOT_ACTIVATED", LocalAgentStates.NotActivated);
        Assert.Equal("CONNECTING", LocalAgentStates.Connecting);
        Assert.Equal("CONNECTED", LocalAgentStates.Connected);
        Assert.Equal("OFFLINE", LocalAgentStates.Offline);
        Assert.Equal("PROBLEM", LocalAgentStates.Problem);
    }

    [Fact]
    public void ContractsSourceStillDeclaresTheSameValues()
    {
        // Cross-check against the canonical source file when the repo is available.
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CloudOrc.WindowsAgents.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        if (dir is null)
        {
            return;
        }

        var contract = Path.Combine(dir, "src", "CloudOrc.Agent.Contracts", "Backup", "WorkerProtocol.cs");
        if (!File.Exists(contract))
        {
            return;
        }

        var text = File.ReadAllText(contract);
        foreach (var literal in new[]
                 {
                     "\"CloudOrc.Agent.Status\"", "\"status\"", "\"activate\"", "\"cancel-activation\"", "\"retry\"",
                     "\"NOT_ACTIVATED\"", "\"CONNECTING\"", "\"CONNECTED\"", "\"OFFLINE\"", "\"PROBLEM\"",
                     "\"deviceName\"", "\"agentVersion\"", "\"lastContactAt\"", "\"retryStatus\"", "\"problem\"",
                     "\"canReconnect\"", "\"diagnosticsId\"", "\"protectionSummary\"", "\"portalUrl\""
                 })
        {
            Assert.Contains(literal, text);
        }
    }
}
