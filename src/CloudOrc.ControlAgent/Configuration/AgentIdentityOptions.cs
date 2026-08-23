namespace CloudOrc.ControlAgent.Configuration;

/// <summary>
/// Root configuration section ("AgentIdentity" in appsettings.json). For this
/// local-testing phase, AgentId/ServerId are just locally configured values - there is no
/// backend-driven enrollment yet. MachineId/MachineName/AgentVersion are derived at
/// runtime by <see cref="Identity.AgentIdentityProvider"/>, not configured here.
/// </summary>
public sealed class AgentIdentityOptions
{
    public const string SectionName = "AgentIdentity";

    public string AgentId { get; set; } = "local-test-agent";

    public string ServerId { get; set; } = "local-test-server";
}
