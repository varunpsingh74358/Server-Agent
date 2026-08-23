using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace CloudOrc.ControlAgent.Identity;

/// <summary>
/// Resolves this machine's stable identifier. Shared by <see cref="AgentIdentityProvider"/>
/// (normal hosted startup) and the <c>enroll</c> CLI mode (which runs before the host is
/// built, so it needs the same logic without a DI container).
/// </summary>
public static class MachineIdResolver
{
    private const string MachineGuidRegistryPath = @"SOFTWARE\Microsoft\Cryptography";
    private const string MachineGuidValueName = "MachineGuid";

    public static string Resolve(string dataDirectory, ILogger logger)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(MachineGuidRegistryPath);
            if (key?.GetValue(MachineGuidValueName) is string guid && !string.IsNullOrWhiteSpace(guid))
            {
                return guid;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            logger.LogWarning(ex, "Could not read the Windows MachineGuid registry value; falling back to a locally-generated machine id.");
        }

        return ResolveOrCreateFallbackMachineId(dataDirectory, logger);
    }

    private static string ResolveOrCreateFallbackMachineId(string dataDirectory, ILogger logger)
    {
        var fallbackFilePath = Path.Combine(dataDirectory, "machine-id.txt");

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
            Directory.CreateDirectory(dataDirectory);
            File.WriteAllText(fallbackFilePath, generated);
            return generated;
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Could not persist a fallback machine id; using a per-process value instead (it will change on restart).");
            return Guid.NewGuid().ToString("D");
        }
    }
}
