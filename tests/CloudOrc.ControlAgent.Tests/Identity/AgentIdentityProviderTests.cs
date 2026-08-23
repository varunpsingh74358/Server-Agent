using CloudOrc.Agent.Contracts.Enrollment;
using CloudOrc.ControlAgent.Configuration;
using CloudOrc.ControlAgent.Enrollment;
using CloudOrc.ControlAgent.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudOrc.ControlAgent.Tests.Identity;

public class AgentIdentityProviderTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "CloudOrcAgentTests-" + Guid.NewGuid().ToString("N"));

    private ControlAgentOptions ControlAgentOptions => new() { DataDirectory = _tempDir };

    private AgentIdentityProvider CreateProvider(AgentIdentityOptions identityOptions) => new(
        Options.Create(identityOptions),
        Options.Create(ControlAgentOptions),
        new EnrolledStateStore(Options.Create(ControlAgentOptions), NullLogger<EnrolledStateStore>.Instance),
        NullLogger<AgentIdentityProvider>.Instance);

    [Fact]
    public void GetIdentity_NoEnrollment_UsesConfiguredAgentIdAndServerId()
    {
        var provider = CreateProvider(new AgentIdentityOptions { AgentId = "configured-agent", ServerId = "configured-server" });

        var identity = provider.GetIdentity();

        Assert.Equal("configured-agent", identity.AgentId);
        Assert.Equal("configured-server", identity.ServerId);
        Assert.Null(identity.Credential);
    }

    [Fact]
    public void GetIdentity_Enrolled_UsesEnrolledAgentIdServerIdAndCredential_NotConfiguredValues()
    {
        var store = new EnrolledStateStore(Options.Create(ControlAgentOptions), NullLogger<EnrolledStateStore>.Instance);
        store.Save(new EnrolledAgentState
        {
            AgentId = "enrolled-agent",
            ServerId = "enrolled-server",
            BackendUrl = "wss://backend.example.test/agent",
            Credential = "enrolled-credential",
            EnrolledAtUtc = DateTimeOffset.UtcNow
        });

        var provider = CreateProvider(new AgentIdentityOptions { AgentId = "configured-agent", ServerId = "configured-server" });

        var identity = provider.GetIdentity();

        Assert.Equal("enrolled-agent", identity.AgentId);
        Assert.Equal("enrolled-server", identity.ServerId);
        Assert.Equal("enrolled-credential", identity.Credential);
    }

    [Fact]
    public void GetIdentity_CalledTwice_ReturnsTheSameAgentId_NeverRegeneratedOnEachCall()
    {
        var store = new EnrolledStateStore(Options.Create(ControlAgentOptions), NullLogger<EnrolledStateStore>.Instance);
        store.Save(new EnrolledAgentState
        {
            AgentId = "stable-agent-id",
            ServerId = "server-1",
            BackendUrl = "wss://backend.example.test/agent",
            Credential = "cred",
            EnrolledAtUtc = DateTimeOffset.UtcNow
        });

        var provider = CreateProvider(new AgentIdentityOptions());

        var first = provider.GetIdentity();
        var second = provider.GetIdentity();

        Assert.Equal(first.AgentId, second.AgentId);
        Assert.Equal("stable-agent-id", second.AgentId);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
