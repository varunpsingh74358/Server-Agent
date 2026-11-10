using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.ControlAgent.Commands;

namespace CloudOrc.ControlAgent.Tests.Commands;

/// <summary>§7: a person pressing "Back up now" waits for the result - never run it at Eco speed.</summary>
public sealed class OnDemandProfileTests
{
    [Theory]
    [InlineData(null, ResourceProfileNames.Eco, ResourceProfileNames.Balanced)]
    [InlineData(null, ResourceProfileNames.Balanced, ResourceProfileNames.Balanced)]
    [InlineData(null, ResourceProfileNames.Throughput, ResourceProfileNames.Throughput)]
    [InlineData(ResourceProfileNames.Throughput, ResourceProfileNames.Eco, ResourceProfileNames.Throughput)]
    [InlineData(ResourceProfileNames.Eco, ResourceProfileNames.Balanced, ResourceProfileNames.Eco)]
    [InlineData("turbo", ResourceProfileNames.Eco, ResourceProfileNames.Balanced)]
    public void OnDemandRuns_UseTheRequestedProfile_OrAtLeastBalanced(string? requested, string policy, string expected)
    {
        Assert.Equal(expected, BackupRunHandler.OnDemandProfile(requested, policy));
    }
}
