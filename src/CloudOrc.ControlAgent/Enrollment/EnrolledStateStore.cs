using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudOrc.Agent.Contracts.Enrollment;
using CloudOrc.ControlAgent.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudOrc.ControlAgent.Enrollment;

/// <summary>
/// Persists/loads the agent's <see cref="EnrolledAgentState"/> under its data directory,
/// encrypted at rest with Windows DPAPI (<see cref="ProtectedData"/>) at
/// <see cref="DataProtectionScope.LocalMachine"/> scope. Machine scope (not
/// <see cref="DataProtectionScope.CurrentUser"/>) is deliberate: enrollment may be
/// performed by the installer running as the interactively-elevated administrator, while
/// the Control Agent service later runs as a different account (typically LocalSystem) -
/// only machine-scoped DPAPI keys are decryptable across that account boundary. The file's
/// NTFS ACLs (inherited from C:\ProgramData\CloudOrc\ControlAgent\) are the remaining
/// access control layer, same as for any other file the agent writes there.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class EnrolledStateStore(IOptions<ControlAgentOptions> options, ILogger<EnrolledStateStore> logger)
{
    private const string FileName = "enrollment.dat";
    private readonly ControlAgentOptions _options = options.Value;

    // DPAPI's optional entropy is an additional, non-secret input mixed into the
    // encryption - it does not need to be secret (it is not a key), only consistent
    // between Protect and Unprotect calls. Using a fixed, project-specific value guards
    // against another DPAPI-using application on the same machine accidentally being able
    // to decrypt this file even though it doesn't know this isn't a secret to protect.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CloudOrc.ControlAgent.Enrollment.v1");

    private string FilePath => Path.Combine(_options.DataDirectory, FileName);

    public bool Exists() => File.Exists(FilePath);

    public EnrolledAgentState? TryLoad()
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }

        try
        {
            var protectedBytes = File.ReadAllBytes(FilePath);
            var jsonBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.LocalMachine);
            return JsonSerializer.Deserialize<EnrolledAgentState>(jsonBytes);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Could not read/decrypt the persisted enrollment state at {Path}. Treating this agent as not enrolled.", FilePath);
            return null;
        }
    }

    public void Save(EnrolledAgentState state)
    {
        Directory.CreateDirectory(_options.DataDirectory);

        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(state);
        var protectedBytes = ProtectedData.Protect(jsonBytes, Entropy, DataProtectionScope.LocalMachine);

        // Write to a temp file then move into place, so a crash mid-write never leaves a
        // half-written (and therefore undecryptable) enrollment file behind.
        var tempPath = FilePath + ".tmp";
        File.WriteAllBytes(tempPath, protectedBytes);
        File.Move(tempPath, FilePath, overwrite: true);
    }

    public void Delete()
    {
        if (File.Exists(FilePath))
        {
            File.Delete(FilePath);
        }
    }
}
