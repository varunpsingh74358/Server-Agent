using System.Reflection;
using CloudOrc.Agent.Contracts.Versioning;

namespace CloudOrc.ControlAgent.Tests.Versioning;

public class AgentVersionInfoTests
{
    [Fact]
    public void GetVersion_ForAnAssemblyWithNoExplicitArgument_ReturnsTheEntryAssemblysInformationalVersion()
    {
        // Deliberately not asserting a hardcoded literal like "1.1.0" - Directory.Build.props'
        // <Version> changes on every release, and this test must keep passing across bumps.
        var expected = (Assembly.GetEntryAssembly() ?? typeof(AgentVersionInfo).Assembly)
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        var actual = AgentVersionInfo.GetVersion();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void GetVersion_GivenAnExplicitAssembly_ReadsThatAssemblysOwnInformationalVersionAttribute_NotTheCallersOrAHardcodedLiteral()
    {
        var contractsAssembly = typeof(AgentVersionInfo).Assembly;
        var expected = contractsAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

        var actual = AgentVersionInfo.GetVersion(contractsAssembly);

        Assert.Equal(expected, actual);
        Assert.NotEqual("0.0.0", actual);
    }

    [Fact]
    public void GetVersion_SameAssemblyAskedTwice_ReturnsAnIdenticalString_SoEveryCallSiteAgrees()
    {
        var assembly = typeof(AgentVersionInfo).Assembly;

        var first = AgentVersionInfo.GetVersion(assembly);
        var second = AgentVersionInfo.GetVersion(assembly);

        Assert.Equal(first, second);
    }
}
