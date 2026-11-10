using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.Agent.Contracts.Enrollment;
using CloudOrc.ControlAgent.DurableQueue;
using CloudOrc.ControlAgent.LocalStatus;
using CloudOrc.ControlAgent.Transport;

namespace CloudOrc.ControlAgent.Tests.LocalStatus;

public class LocalStatusBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);

    private static readonly EnrolledAgentState Enrolled = new()
    {
        AgentId = "agent-1", ServerId = "srv", BackendUrl = "wss://x.example.test/agent", Credential = "c", EnrolledAtUtc = Now,
        PortalUrl = "https://portal.example.test/"
    };

    private static LocalStatusInputs Inputs(
        EnrolledAgentState? enrolled = null,
        bool connected = false,
        bool stopped = false,
        AuthorizationHealth auth = AuthorizationHealth.Ok,
        DateTimeOffset? lastContact = null,
        DateTimeOffset? startedAt = null,
        bool activating = false,
        int policies = 0,
        DateTimeOffset? lastSuccess = null,
        DateTimeOffset? lastVerified = null,
        BackupJobRecord? running = null) =>
        new(enrolled, true, connected, stopped, auth, "retrying", lastContact, startedAt ?? Now.AddMinutes(-10), Now,
            activating, false, null, "install-1", "1.2.3", null, policies, lastSuccess, lastVerified, running, null);

    [Fact]
    public void NotEnrolled_IsNotActivated() =>
        Assert.Equal(LocalAgentStates.NotActivated, LocalStatusBuilder.Build(Inputs()).State);

    [Fact]
    public void ActivationInProgress_IsConnecting() =>
        Assert.Equal(LocalAgentStates.Connecting, LocalStatusBuilder.Build(Inputs(activating: true)).State);

    [Fact]
    public void Connected_ShowsPortal_AndProtectionSummary()
    {
        var status = LocalStatusBuilder.Build(Inputs(Enrolled, connected: true, policies: 1, lastSuccess: Now.AddHours(-2), lastVerified: Now.AddHours(-2)));
        Assert.Equal(LocalAgentStates.Connected, status.State);
        Assert.Equal("https://portal.example.test/", status.PortalUrl);
        Assert.StartsWith("Protected - last backup", status.ProtectionSummary);
        Assert.Contains("verified", status.ProtectionSummary);
    }

    [Fact]
    public void Revoked_IsProblem_WithReconnect()
    {
        var status = LocalStatusBuilder.Build(Inputs(Enrolled, stopped: true, auth: AuthorizationHealth.Revoked));
        Assert.Equal(LocalAgentStates.Problem, status.State);
        Assert.True(status.CanReconnect);
        Assert.Contains("revoked", status.Problem);
    }

    [Fact]
    public void JustStarted_NoContactYet_IsConnecting_LaterOffline()
    {
        Assert.Equal(LocalAgentStates.Connecting, LocalStatusBuilder.Build(Inputs(Enrolled, startedAt: Now.AddSeconds(-30))).State);
        Assert.Equal(LocalAgentStates.Offline, LocalStatusBuilder.Build(Inputs(Enrolled, startedAt: Now.AddMinutes(-10))).State);
    }

    [Fact]
    public void RunningJob_IsSummarized()
    {
        var running = new BackupJobRecord
        {
            JobId = "j", Kind = BackupJobKinds.Backup, IdempotencyKey = "k", CorrelationId = "c",
            State = BackupJobStates.Transferring, NotBefore = Now, CreatedAt = Now
        };
        Assert.Equal("Backup in progress (transferring)", LocalStatusBuilder.Build(Inputs(Enrolled, connected: true, policies: 1, running: running)).ProtectionSummary);
    }

    [Fact]
    public void NoPolicy_SaysNotConfigured() =>
        Assert.Equal("Backup not configured for this device", LocalStatusBuilder.Build(Inputs(Enrolled, connected: true)).ProtectionSummary);

    [Fact]
    public void DiagnosticsId_IsStable_AndRevealsNothing()
    {
        var a = LocalStatusBuilder.DiagnosticsId("install-1", "agent-1");
        Assert.Equal(a, LocalStatusBuilder.DiagnosticsId("install-1", "agent-1"));
        Assert.StartsWith("CO-", a);
        Assert.DoesNotContain("install", a);
        Assert.DoesNotContain("agent", a);
    }

    [Fact]
    public void NonHttpsPortalUrls_AreNeverOffered()
    {
        var insecure = new EnrolledAgentState
        {
            AgentId = "a", ServerId = "s", BackendUrl = "wss://x.example.test/agent", Credential = "c", EnrolledAtUtc = Now, PortalUrl = "http://portal.example.test/"
        };
        Assert.Null(LocalStatusBuilder.Build(Inputs(insecure, connected: true)).PortalUrl);
    }
}
