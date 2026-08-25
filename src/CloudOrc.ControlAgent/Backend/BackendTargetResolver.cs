using CloudOrc.ControlAgent.Configuration;

namespace CloudOrc.ControlAgent.Backend;

/// <summary>
/// One fully-resolved backend connection this agent should maintain - either the primary
/// connection (enrollment-issued or manually configured) or one of
/// <see cref="BackendConnectionOptions.AdditionalTargets"/>.
/// </summary>
public sealed record ResolvedBackendTarget(
    string Name,
    string Url,
    string? Credential,
    bool DevelopmentAllowInsecureWs,
    bool FromEnrollment);

/// <summary>
/// Turns the primary Url/Enabled fields plus any configured
/// <see cref="BackendConnectionOptions.AdditionalTargets"/> into the final list of backend
/// targets this agent should connect to. Pure and side-effect free so the validation rules
/// (unique names, no unlabeled insecure ws://) are unit-testable without spinning up
/// sockets or the full host - mirrors the existing ws:// guard rail Program.cs already
/// applied to the single primary connection, just generalized across every target.
/// </summary>
public static class BackendTargetResolver
{
    /// <summary>Reserved name for the primary connection - matches <see cref="OutgoingMessageChannel.DefaultTargetName"/>.</summary>
    public const string PrimaryTargetName = OutgoingMessageChannel.DefaultTargetName;

    public static IReadOnlyList<ResolvedBackendTarget> Resolve(
        BackendConnectionOptions options,
        string? enrolledCredential,
        bool primaryUrlIsFromEnrollment)
    {
        var targets = new List<ResolvedBackendTarget>();

        if (options.Enabled && !string.IsNullOrWhiteSpace(options.Url))
        {
            targets.Add(new ResolvedBackendTarget(
                PrimaryTargetName,
                options.Url,
                enrolledCredential,
                options.DevelopmentAllowInsecureWs,
                primaryUrlIsFromEnrollment));
        }

        foreach (var extra in options.AdditionalTargets)
        {
            if (!extra.Enabled || string.IsNullOrWhiteSpace(extra.Url))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(extra.Name))
            {
                throw new InvalidOperationException(
                    "Each BackendConnection.AdditionalTargets entry must have a non-empty Name.");
            }

            if (string.Equals(extra.Name, PrimaryTargetName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"BackendConnection.AdditionalTargets entry name '{extra.Name}' is reserved for the " +
                    "primary connection - choose a different Name.");
            }

            targets.Add(new ResolvedBackendTarget(
                extra.Name,
                extra.Url,
                string.IsNullOrEmpty(extra.Credential) ? null : extra.Credential,
                extra.DevelopmentAllowInsecureWs,
                FromEnrollment: false));
        }

        var duplicate = targets
            .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Duplicate backend target name '{duplicate.Key}' - target names must be unique.");
        }

        foreach (var target in targets)
        {
            var isInsecureWs = target.Url.StartsWith("ws://", StringComparison.OrdinalIgnoreCase);
            if (isInsecureWs && !target.DevelopmentAllowInsecureWs && !target.FromEnrollment)
            {
                throw new InvalidOperationException(
                    $"Backend target '{target.Name}' URL ('{target.Url}') uses insecure ws://, but its " +
                    "DevelopmentAllowInsecureWs is false. Either use a wss:// URL, or set " +
                    "DevelopmentAllowInsecureWs to true for LOCAL TESTING ONLY. Refusing to start.");
            }
        }

        return targets;
    }
}
