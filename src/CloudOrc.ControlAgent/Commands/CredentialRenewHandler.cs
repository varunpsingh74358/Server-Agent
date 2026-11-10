using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.ControlAgent.Transport;

namespace CloudOrc.ControlAgent.Commands;

/// <summary>
/// agent.credential.renew - the backend asks the agent to rotate its session credential (§4
/// "mTLS credential issuance and rotation", §10 short-lived session credentials) by proving
/// possession of the device key. The new credential is persisted DPAPI-protected.
/// </summary>
public sealed class CredentialRenewHandler(ISessionRenewer renewer) : ITypedCommandHandler
{
    public string CommandType => BackupCommandTypes.AgentCredentialRenew;

    public async Task<TypedCommandAcceptance> HandleAsync(TypedCommandContext context, CancellationToken cancellationToken)
    {
        if (!renewer.IsAvailable)
        {
            return TypedCommandDispatcher.Rejected("RENEWAL_NOT_AVAILABLE", "This device has no device key; re-activation is required to rotate credentials.");
        }

        var outcome = await renewer.RenewAsync(cancellationToken).ConfigureAwait(false);
        return outcome == RenewalOutcome.Renewed
            ? new TypedCommandAcceptance { Accepted = true, Message = "Session credential renewed." }
            : TypedCommandDispatcher.Rejected("RENEWAL_FAILED", $"Session renewal {outcome.ToString().ToLowerInvariant()}.");
    }
}
