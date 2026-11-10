using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.Agent.Contracts.Enrollment;
using CloudOrc.ControlAgent.DurableQueue;
using CloudOrc.ControlAgent.Transport;

namespace CloudOrc.ControlAgent.LocalStatus;

/// <summary>Inputs for <see cref="LocalStatusBuilder.Build"/> - all plain values so it is pure and testable.</summary>
public sealed record LocalStatusInputs(
    EnrolledAgentState? Enrolled,
    bool BackendEnabled,
    bool LinkConnected,
    bool LinkStopped,
    AuthorizationHealth Authorization,
    string? RetryStatus,
    DateTimeOffset? LastContactAt,
    DateTimeOffset ServiceStartedAt,
    DateTimeOffset Now,
    bool ActivationInProgress,
    bool RestartPending,
    string? ActivationError,
    string InstallationId,
    string AgentVersion,
    string? FallbackPortalUrl,
    int PolicyCount,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastVerifiedAt,
    BackupJobRecord? RunningJob,
    BackupJobRecord? LastFailedJob);

/// <summary>
/// Builds the §4 local status: exactly the four screens' worth of information and nothing else -
/// no schedule, retention, source paths, keys, destinations or restore controls.
/// </summary>
public static class LocalStatusBuilder
{
    private static readonly TimeSpan ConnectingGrace = TimeSpan.FromMinutes(2);

    public static LocalStatusResponse Build(LocalStatusInputs i, bool ok = true, string? error = null)
    {
        var diagnosticsId = DiagnosticsId(i.InstallationId, i.Enrolled?.AgentId);
        var portal = i.Enrolled?.PortalUrl ?? i.FallbackPortalUrl;

        string state;
        string? problem = null;
        var canReconnect = false;
        string? retry = i.RetryStatus;

        if (i.ActivationInProgress || i.RestartPending)
        {
            state = LocalAgentStates.Connecting;
            retry = i.RestartPending ? "Activated - starting the connection" : "Registering this device";
        }
        else if (i.Enrolled is null)
        {
            state = LocalAgentStates.NotActivated;
        }
        else if (!i.BackendEnabled)
        {
            state = LocalAgentStates.Problem;
            problem = "The agent is activated but its connection is disabled in configuration.";
        }
        else if (i.LinkConnected)
        {
            state = LocalAgentStates.Connected;
        }
        else if (i.LinkStopped || i.Authorization is AuthorizationHealth.Revoked or AuthorizationHealth.Expired)
        {
            state = LocalAgentStates.Problem;
            canReconnect = true;
            problem = i.Authorization == AuthorizationHealth.Revoked
                ? "This device's access to CloudOrc was revoked. Create a new activation code in the portal to reconnect."
                : "This device's authorization expired. Create a new activation code in the portal to reconnect.";
        }
        else if (i.LastContactAt is null && i.Now - i.ServiceStartedAt < ConnectingGrace)
        {
            state = LocalAgentStates.Connecting;
        }
        else
        {
            state = LocalAgentStates.Offline;
        }

        return new LocalStatusResponse
        {
            Ok = ok,
            Error = error ?? (state == LocalAgentStates.NotActivated ? i.ActivationError : null),
            State = state,
            DeviceName = Environment.MachineName,
            AgentVersion = i.AgentVersion,
            LastContactAt = i.LastContactAt,
            RetryStatus = state is LocalAgentStates.Connecting or LocalAgentStates.Offline ? retry : null,
            Problem = problem,
            CanReconnect = canReconnect,
            DiagnosticsId = diagnosticsId,
            ProtectionSummary = i.Enrolled is null ? null : ProtectionSummary(i),
            PortalUrl = portal is not null && Uri.TryCreate(portal, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? portal : null
        };
    }

    public static string ProtectionSummary(LocalStatusInputs i)
    {
        if (i.RunningJob is { } running)
        {
            var label = running.Kind switch
            {
                BackupJobKinds.Restore => "Restore in progress",
                BackupJobKinds.Verify => "Verifying backup",
                BackupJobKinds.Maintenance => "Backup maintenance in progress",
                _ => "Backup in progress"
            };
            return $"{label} ({running.State.ToLowerInvariant()})";
        }

        if (i.PolicyCount == 0)
        {
            return "Backup not configured for this device";
        }

        if (i.LastSuccessAt is null)
        {
            return i.LastFailedJob is not null ? "Not yet protected - last backup attempt failed" : "Waiting for the first backup";
        }

        var when = i.LastSuccessAt.Value.ToLocalTime().ToString("dd MMM HH:mm", CultureInfo.CurrentCulture);
        var verified = i.LastVerifiedAt is not null && i.LastVerifiedAt >= i.LastSuccessAt ? ", verified" : "";
        var attention = i.LastFailedJob is not null && i.LastFailedJob.EndedAt > i.LastSuccessAt ? " - latest attempt failed" : "";
        return $"Protected - last backup {when}{verified}{attention}";
    }

    /// <summary>Stable, non-secret id support can correlate with server logs.</summary>
    public static string DiagnosticsId(string installationId, string? agentId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{installationId}|{agentId}"));
        return "CO-" + Convert.ToHexString(hash, 0, 6);
    }
}
