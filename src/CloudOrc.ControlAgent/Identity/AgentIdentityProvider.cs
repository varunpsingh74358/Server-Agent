using CloudOrc.Agent.Contracts.Identity;
using CloudOrc.Agent.Contracts.Versioning;
using CloudOrc.ControlAgent.Configuration;
using CloudOrc.ControlAgent.Enrollment;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudOrc.ControlAgent.Identity;

/// <summary>
/// Builds this process's <see cref="AgentIdentity"/>. When this agent has been enrolled
/// (see <see cref="EnrolledStateStore"/>), AgentId/ServerId/Credential come from the
/// persisted, backend-issued enrollment state - never from configuration. Falling back to
/// <see cref="AgentIdentityOptions"/> is only for the pre-enrollment local-testing flow
/// that predates enrollment (see docs/FUTURE_BACKEND_INTEGRATION.md history) and remains
/// supported for local/dev use. MachineId prefers the stable Windows-wide "MachineGuid",
/// falling back to a GUID generated once and persisted under the agent's data directory.
/// </summary>
public sealed class AgentIdentityProvider
{
    private readonly AgentIdentityOptions _identityOptions;
    private readonly ControlAgentOptions _controlAgentOptions;
    private readonly EnrolledStateStore _enrolledStateStore;
    private readonly ILogger<AgentIdentityProvider> _logger;

    public AgentIdentityProvider(
        IOptions<AgentIdentityOptions> identityOptions,
        IOptions<ControlAgentOptions> controlAgentOptions,
        EnrolledStateStore enrolledStateStore,
        ILogger<AgentIdentityProvider> logger)
    {
        _identityOptions = identityOptions.Value;
        _controlAgentOptions = controlAgentOptions.Value;
        _enrolledStateStore = enrolledStateStore;
        _logger = logger;
    }

    public AgentIdentity GetIdentity()
    {
        var enrolled = _enrolledStateStore.TryLoad();
        var machineId = MachineIdResolver.Resolve(_controlAgentOptions.DataDirectory, _logger);
        var agentVersion = AgentVersionInfo.GetVersion(typeof(AgentIdentityProvider).Assembly);

        if (enrolled is not null)
        {
            return new AgentIdentity
            {
                AgentId = enrolled.AgentId,
                ServerId = enrolled.ServerId,
                MachineId = machineId,
                MachineName = Environment.MachineName,
                AgentVersion = agentVersion,
                Credential = enrolled.Credential
            };
        }

        return new AgentIdentity
        {
            AgentId = _identityOptions.AgentId,
            ServerId = _identityOptions.ServerId,
            MachineId = machineId,
            MachineName = Environment.MachineName,
            AgentVersion = agentVersion,
            Credential = null
        };
    }
}
