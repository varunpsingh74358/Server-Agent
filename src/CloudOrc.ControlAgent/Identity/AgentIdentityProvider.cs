using CloudOrc.Agent.Contracts.Identity;
using CloudOrc.ControlAgent.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Win32;

namespace CloudOrc.ControlAgent.Identity;

/// <summary>
/// Builds this process's <see cref="AgentIdentity"/>. AgentId/ServerId come straight from
/// configuration (no enrollment yet - see docs/FUTURE_BACKEND_INTEGRATION.md). MachineId
/// prefers the stable Windows-wide "MachineGuid" (set once by Windows Setup and stable
/// across reboots/reinstalls of this agent), falling back to a GUID generated once and
/// persisted under the agent's data directory if the registry key cannot be read (e.g.
/// restricted service account permissions).
/// </summary>
public sealed class AgentIdentityProvider
{
    private const string MachineGuidRegistryPath = @"SOFTWARE\Microsoft\Cryptography";
    private const string MachineGuidValueName = "MachineGuid";

    private readonly AgentIdentityOptions _identityOptions;
    private readonly ControlAgentOptions _controlAgentOptions;
    private readonly ILogger<AgentIdentityProvider> _logger;

    public AgentIdentityProvider(
        IOptions<AgentIdentityOptions> identityOptions,
        IOptions<ControlAgentOptions> controlAgentOptions,
        ILogger<AgentIdentityProvider> logger)
    {
        _identityOptions = identityOptions.Value;
        _controlAgentOptions = controlAgentOptions.Value;
        _logger = logger;
    }

    public AgentIdentity GetIdentity() => new()
    {
        AgentId = _identityOptions.AgentId,
        ServerId = _identityOptions.ServerId,
        MachineId = ResolveMachineId(),
        MachineName = Environment.MachineName,
        AgentVersion = typeof(AgentIdentityProvider).Assembly.GetName().Version?.ToString() ?? "0.0.0"
    };

    private string ResolveMachineId()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(MachineGuidRegistryPath);
            if (key?.GetValue(MachineGuidValueName) is string guid && !string.IsNullOrWhiteSpace(guid))
            {
                return guid;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            _logger.LogWarning(ex, "Could not read the Windows MachineGuid registry value; falling back to a locally-generated machine id.");
        }

        return ResolveOrCreateFallbackMachineId();
    }

    private string ResolveOrCreateFallbackMachineId()
    {
        var fallbackFilePath = Path.Combine(_controlAgentOptions.DataDirectory, "machine-id.txt");

        try
        {
            if (File.Exists(fallbackFilePath))
            {
                var existing = File.ReadAllText(fallbackFilePath).Trim();
                if (!string.IsNullOrWhiteSpace(existing))
                {
                    return existing;
                }
            }

            var generated = Guid.NewGuid().ToString("D");
            Directory.CreateDirectory(_controlAgentOptions.DataDirectory);
            File.WriteAllText(fallbackFilePath, generated);
            return generated;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not persist a fallback machine id; using a per-process value instead (it will change on restart).");
            return Guid.NewGuid().ToString("D");
        }
    }
}
