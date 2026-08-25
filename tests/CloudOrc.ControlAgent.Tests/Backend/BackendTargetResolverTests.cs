using CloudOrc.ControlAgent.Backend;
using CloudOrc.ControlAgent.Configuration;

namespace CloudOrc.ControlAgent.Tests.Backend;

public class BackendTargetResolverTests
{
    [Fact]
    public void Resolve_OnlyPrimaryConfigured_ReturnsSingleDefaultTarget()
    {
        var options = new BackendConnectionOptions { Enabled = true, Url = "wss://backend.example/agent" };

        var targets = BackendTargetResolver.Resolve(options, enrolledCredential: "cred-123", primaryUrlIsFromEnrollment: true);

        var target = Assert.Single(targets);
        Assert.Equal(BackendTargetResolver.PrimaryTargetName, target.Name);
        Assert.Equal("wss://backend.example/agent", target.Url);
        Assert.Equal("cred-123", target.Credential);
        Assert.True(target.FromEnrollment);
    }

    [Fact]
    public void Resolve_PrimaryDisabled_ReturnsNoPrimaryTarget()
    {
        var options = new BackendConnectionOptions { Enabled = false, Url = "wss://backend.example/agent" };

        var targets = BackendTargetResolver.Resolve(options, null, false);

        Assert.Empty(targets);
    }

    [Fact]
    public void Resolve_PrimaryPlusAdditionalTarget_ReturnsBoth()
    {
        var options = new BackendConnectionOptions
        {
            Enabled = true,
            Url = "wss://prod.example/agent",
            AdditionalTargets =
            [
                new BackendTargetOptions { Name = "dev-tunnel", Url = "wss://dev-tunnel.example/agent" }
            ]
        };

        var targets = BackendTargetResolver.Resolve(options, "prod-cred", primaryUrlIsFromEnrollment: true);

        Assert.Equal(2, targets.Count);
        Assert.Contains(targets, t => t.Name == BackendTargetResolver.PrimaryTargetName && t.Url == "wss://prod.example/agent" && t.Credential == "prod-cred");
        Assert.Contains(targets, t => t.Name == "dev-tunnel" && t.Url == "wss://dev-tunnel.example/agent" && t.Credential == null);
    }

    [Fact]
    public void Resolve_DisabledAdditionalTarget_IsSkipped()
    {
        var options = new BackendConnectionOptions
        {
            AdditionalTargets = [new BackendTargetOptions { Name = "dev-tunnel", Url = "wss://dev-tunnel.example/agent", Enabled = false }]
        };

        var targets = BackendTargetResolver.Resolve(options, null, false);

        Assert.Empty(targets);
    }

    [Fact]
    public void Resolve_AdditionalTargetWithEmptyUrl_IsSkipped()
    {
        var options = new BackendConnectionOptions
        {
            AdditionalTargets = [new BackendTargetOptions { Name = "dev-tunnel", Url = "" }]
        };

        var targets = BackendTargetResolver.Resolve(options, null, false);

        Assert.Empty(targets);
    }

    [Fact]
    public void Resolve_AdditionalTargetWithEmptyName_Throws()
    {
        var options = new BackendConnectionOptions
        {
            AdditionalTargets = [new BackendTargetOptions { Name = "", Url = "wss://dev-tunnel.example/agent" }]
        };

        Assert.Throws<InvalidOperationException>(() => BackendTargetResolver.Resolve(options, null, false));
    }

    [Fact]
    public void Resolve_AdditionalTargetReusingReservedDefaultName_Throws()
    {
        var options = new BackendConnectionOptions
        {
            AdditionalTargets = [new BackendTargetOptions { Name = "default", Url = "wss://dev-tunnel.example/agent" }]
        };

        Assert.Throws<InvalidOperationException>(() => BackendTargetResolver.Resolve(options, null, false));
    }

    [Fact]
    public void Resolve_TwoAdditionalTargetsWithSameName_Throws()
    {
        var options = new BackendConnectionOptions
        {
            AdditionalTargets =
            [
                new BackendTargetOptions { Name = "dev-tunnel", Url = "wss://a.example/agent" },
                new BackendTargetOptions { Name = "dev-tunnel", Url = "wss://b.example/agent" }
            ]
        };

        Assert.Throws<InvalidOperationException>(() => BackendTargetResolver.Resolve(options, null, false));
    }

    [Fact]
    public void Resolve_AdditionalTargetWithInsecureWsAndNoOptIn_Throws()
    {
        var options = new BackendConnectionOptions
        {
            AdditionalTargets = [new BackendTargetOptions { Name = "dev-tunnel", Url = "ws://dev-tunnel.example/agent", DevelopmentAllowInsecureWs = false }]
        };

        Assert.Throws<InvalidOperationException>(() => BackendTargetResolver.Resolve(options, null, false));
    }

    [Fact]
    public void Resolve_AdditionalTargetWithInsecureWsAndOptIn_Succeeds()
    {
        var options = new BackendConnectionOptions
        {
            AdditionalTargets = [new BackendTargetOptions { Name = "dev-tunnel", Url = "ws://dev-tunnel.example/agent", DevelopmentAllowInsecureWs = true }]
        };

        var targets = BackendTargetResolver.Resolve(options, null, false);

        var target = Assert.Single(targets);
        Assert.Equal("dev-tunnel", target.Name);
    }

    [Fact]
    public void Resolve_AdditionalTargetWithOwnCredential_KeepsItSeparateFromPrimary()
    {
        var options = new BackendConnectionOptions
        {
            Enabled = true,
            Url = "wss://prod.example/agent",
            AdditionalTargets = [new BackendTargetOptions { Name = "dev-tunnel", Url = "wss://dev-tunnel.example/agent", Credential = "dev-cred" }]
        };

        var targets = BackendTargetResolver.Resolve(options, enrolledCredential: "prod-cred", primaryUrlIsFromEnrollment: true);

        Assert.Equal("prod-cred", targets.Single(t => t.Name == BackendTargetResolver.PrimaryTargetName).Credential);
        Assert.Equal("dev-cred", targets.Single(t => t.Name == "dev-tunnel").Credential);
    }
}
