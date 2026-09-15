using System.Diagnostics;
using System.Security;
using CloudOrc.Agent.Contracts.Abstractions;
using CloudOrc.Agent.Contracts.Commands;
using CloudOrc.Agent.Contracts.Execution;
using Microsoft.Extensions.Logging;

namespace CloudOrc.ControlAgent.RdpTransferPolicy;

/// <summary>
/// Implements CommandTypes.RdpTransferPolicyApply: detect OS capability, apply the
/// Windows-native RDS policy registry values via <see cref="IRdpPolicyRegistryAccessor"/>,
/// read back the actual state, and only then decide the result. A registry write
/// succeeding is never enough on its own to report success - see
/// <see cref="ApplyAsync"/> for the verification step.
///
/// Result-status rules (matching the desired-state contract, which is always "plain-text-
/// only clipboard both directions + drive redirection blocked"):
///   - Directional clipboard unsupported on this OS build, drive redirection verified
///     blocked: Partial (a known, expected OS limitation, not a failure).
///   - Directional clipboard unsupported AND drive redirection could not be verified
///     blocked: Failed (nothing meaningful was achieved).
///   - Directional clipboard supported: every write is read back and compared; any
///     mismatch (registry API reported success but the value doesn't read back as
///     requested) is Failed, never Partial - that is an unexpected problem, not a known
///     limitation.
///   - Everything requested verified as desired: Success.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsRdpTransferPolicyManager(
    IRdpPolicyRegistryAccessor registry,
    IRdpOsBuildDetector buildDetector,
    ILogger<WindowsRdpTransferPolicyManager> logger) : IRdpTransferPolicyExecutor
{
    public Task<RdpTransferPolicyOutcome> ApplyAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var output = new List<string>();
        var desired = RdpTransferDesiredState.PlainTextOnlyClipboardAndBlockedDrive;

        var osInfo = DetectBuildCapability(output);

        var driveResult = TryApplyDriveRedirectionBlock(desired, output);
        var anyWriteOccurred = driveResult.WriteOccurred;

        PolicyItemResult clientToServerResult;
        PolicyItemResult serverToClientResult;

        if (!osInfo.SupportsDirectionalClipboard)
        {
            const string reason = "Directional plain-text-only clipboard policy is not supported on this Windows build.";
            output.Add($"clientToServerClipboard=UNSUPPORTED ({reason})");
            output.Add($"serverToClientClipboard=UNSUPPORTED ({reason})");
            clientToServerResult = PolicyItemResult.Unsupported();
            serverToClientResult = PolicyItemResult.Unsupported();
        }
        else
        {
            clientToServerResult = TryApplyClipboardLevel(
                desired.ClipboardLevel, output, "clientToServerClipboard",
                registry.ReadClientToServerClipboardLevel, registry.WriteClientToServerClipboardLevel);
            serverToClientResult = TryApplyClipboardLevel(
                desired.ClipboardLevel, output, "serverToClientClipboard",
                registry.ReadServerToClientClipboardLevel, registry.WriteServerToClientClipboardLevel);

            anyWriteOccurred = anyWriteOccurred || clientToServerResult.WriteOccurred || serverToClientResult.WriteOccurred;
        }

        var status = DetermineStatus(driveResult, clientToServerResult, serverToClientResult, osInfo.SupportsDirectionalClipboard);
        var requiresNewSession = (status is CommandStatus.Success or CommandStatus.Partial) && anyWriteOccurred;
        output.Add($"requiresNewSession={requiresNewSession}");

        var errors = new List<string?> { driveResult.FailureReason, clientToServerResult.FailureReason, serverToClientResult.FailureReason }
            .Where(e => e is not null)
            .ToList();

        stopwatch.Stop();
        logger.LogInformation(
            "RDP transfer policy apply for command {CommandId}: status={Status}, os={Os}, requiresNewSession={RequiresNewSession}.",
            request.CommandId, status, osInfo.FullVersion, requiresNewSession);

        return Task.FromResult(new RdpTransferPolicyOutcome
        {
            Status = status,
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            DurationMilliseconds = stopwatch.ElapsedMilliseconds,
            Output = output,
            Error = errors.Count > 0 ? string.Join(" | ", errors) : null,
            RequiresNewSession = requiresNewSession
        });
    }

    private RdpOsBuildInfo DetectBuildCapability(List<string> output)
    {
        var osInfo = buildDetector.DetectCurrent();
        output.Add($"os={osInfo.ProductName} build={osInfo.FullVersion}");
        output.Add($"directionalClipboardCapability={(osInfo.SupportsDirectionalClipboard ? "supported" : "unsupported")}");
        return osInfo;
    }

    private PolicyItemResult TryApplyDriveRedirectionBlock(RdpTransferDesiredState desired, List<string> output)
    {
        try
        {
            var before = registry.ReadDriveRedirectionBlocked();
            var writeOccurred = false;
            if (before != desired.DriveRedirectionBlocked)
            {
                registry.WriteDriveRedirectionBlocked(desired.DriveRedirectionBlocked);
                writeOccurred = true;
            }

            var after = registry.ReadDriveRedirectionBlocked();
            output.Add($"driveRedirection: desired=blocked actual={(after ? "blocked" : "allowed")}");

            return after == desired.DriveRedirectionBlocked
                ? PolicyItemResult.Ok(writeOccurred)
                : PolicyItemResult.Failed("Verification failed: drive redirection did not read back as blocked after being applied.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            logger.LogError(ex, "Failed to apply the drive redirection block policy.");
            return PolicyItemResult.Failed($"Failed to apply drive redirection policy: {ex.Message}");
        }
    }

    private PolicyItemResult TryApplyClipboardLevel(
        RdpClipboardRedirectionLevel desiredLevel,
        List<string> output,
        string label,
        Func<RdpClipboardRedirectionLevel?> read,
        Action<RdpClipboardRedirectionLevel> write)
    {
        try
        {
            var before = read();
            var writeOccurred = false;
            if (before != desiredLevel)
            {
                write(desiredLevel);
                writeOccurred = true;
            }

            var after = read();
            output.Add($"{label}: desired={desiredLevel} actual={(after is null ? "NOT_CONFIGURED" : after.ToString())}");

            return after == desiredLevel
                ? PolicyItemResult.Ok(writeOccurred)
                : PolicyItemResult.Failed($"Verification failed: {label} did not read back as {desiredLevel} after being applied.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            logger.LogError(ex, "Failed to apply the {Label} clipboard policy.", label);
            return PolicyItemResult.Failed($"Failed to apply {label} policy: {ex.Message}");
        }
    }

    private static CommandStatus DetermineStatus(
        PolicyItemResult drive,
        PolicyItemResult clientToServer,
        PolicyItemResult serverToClient,
        bool directionalClipboardSupported)
    {
        if (!directionalClipboardSupported)
        {
            // Clipboard is categorically unsupported here, not failed - only drive
            // redirection's actual apply/verify outcome decides Partial vs. Failed.
            return drive.Succeeded ? CommandStatus.Partial : CommandStatus.Failed;
        }

        if (drive.Succeeded && clientToServer.Succeeded && serverToClient.Succeeded)
        {
            return CommandStatus.Success;
        }

        // Every requested policy was supported here, so any mismatch is an unexpected
        // failure to investigate, never a "partial but fine" outcome.
        return CommandStatus.Failed;
    }

    private readonly record struct PolicyItemResult(bool Succeeded, bool WriteOccurred, string? FailureReason)
    {
        public static PolicyItemResult Ok(bool writeOccurred) => new(true, writeOccurred, null);

        public static PolicyItemResult Failed(string reason) => new(false, false, reason);

        public static PolicyItemResult Unsupported() => new(false, false, null);
    }
}
