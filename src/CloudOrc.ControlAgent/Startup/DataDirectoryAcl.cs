using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace CloudOrc.ControlAgent.Startup;

/// <summary>
/// Restricts the agent's data directory to SYSTEM + Administrators (no inheritance from
/// ProgramData, where BUILTIN\Users may read and create files). Covers command files, job work
/// folders (engine config), caches, manifests, state and logs - §10 "no plaintext keys ... in
/// support diagnostics", §13 "OS-protected credentials". Without it any local user could drop a
/// command file that runs as SYSTEM, or read engine configuration and file manifests.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DataDirectoryAcl
{
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    /// <summary>Returns null on success, otherwise the reason it could not be applied (never throws).</summary>
    public static string? Harden(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(System, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(directory).SetAccessControl(security);
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or IOException or PrivilegeNotHeldException)
        {
            return ex.Message;
        }
    }

    private const FileSystemRights WriteRights =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes |
        FileSystemRights.WriteAttributes | FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    /// <summary>
    /// True when nobody but SYSTEM, Administrators, TrustedInstaller, CREATOR OWNER or the agent's own
    /// account can create or change files in <paramref name="directory"/>. Used as a fail-safe before
    /// executing local command files: if the startup hardening could not be applied (or was undone),
    /// local commands are not executed at all.
    /// </summary>
    public static bool IsWritableOnlyByPrivileged(string directory, out string? offender)
    {
        offender = null;
        try
        {
            using var self = WindowsIdentity.GetCurrent();
            var rules = new DirectoryInfo(directory).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier));
            foreach (FileSystemAccessRule rule in rules)
            {
                if (rule.AccessControlType != AccessControlType.Allow || (rule.FileSystemRights & WriteRights) == 0)
                {
                    continue;
                }

                var sid = (SecurityIdentifier)rule.IdentityReference;
                if (sid.Equals(System) || sid.Equals(Administrators) || sid.Equals(self.User)
                    || sid.IsWellKnown(WellKnownSidType.CreatorOwnerSid)
                    || sid.Value == "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464") // TrustedInstaller
                {
                    continue;
                }

                offender = sid.Value;
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            offender = ex.Message;
            return false;
        }
    }
}
