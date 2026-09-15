namespace CloudOrc.Agent.Contracts.Commands;

/// <summary>
/// Pure, side-effect-free validation of a <see cref="CommandRequest"/>. Kept independent
/// of any file system or transport concerns so it is trivially unit testable.
/// </summary>
public static class CommandRequestValidator
{
    public static CommandValidationResult Validate(CommandRequest? request, CommandValidationOptions options)
    {
        if (request is null)
        {
            return CommandValidationResult.Failure("Command payload could not be parsed.");
        }

        if (string.IsNullOrWhiteSpace(request.CommandId))
        {
            return CommandValidationResult.Failure("CommandId is required and cannot be empty.");
        }

        if (request.CommandId.Length > 200 || request.CommandId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return CommandValidationResult.Failure("CommandId must be a valid file-name-safe string of 200 characters or fewer.");
        }

        return string.Equals(request.CommandType, CommandTypes.RdpTransferPolicyApply, StringComparison.Ordinal)
            ? ValidateRdpTransferPolicyApply(request, options)
            : ValidatePowerShellExec(request, options);
    }

    private static CommandValidationResult ValidatePowerShellExec(CommandRequest request, CommandValidationOptions options)
    {
        if (string.IsNullOrWhiteSpace(request.Script))
        {
            return CommandValidationResult.Failure("Script is required and cannot be empty.");
        }

        if (request.Script.Length > options.MaxScriptLength)
        {
            return CommandValidationResult.Failure($"Script exceeds the maximum allowed length of {options.MaxScriptLength} characters.");
        }

        return ValidateTimeout(request, options);
    }

    /// <summary>
    /// CommandTypes.RdpTransferPolicyApply carries a desired-state payload instead of a
    /// script. Today's desired state is fixed by design: plain-text-only clipboard in both
    /// directions and blocked drive redirection - so validation rejects anything else
    /// rather than accepting an unsupported combination only to fail later during apply.
    /// </summary>
    private static CommandValidationResult ValidateRdpTransferPolicyApply(CommandRequest request, CommandValidationOptions options)
    {
        const string RequiredClipboardMode = "PLAIN_TEXT_ONLY";

        if (!string.Equals(request.ClientToServerClipboard, RequiredClipboardMode, StringComparison.Ordinal))
        {
            return CommandValidationResult.Failure($"clientToServerClipboard must be '{RequiredClipboardMode}'.");
        }

        if (!string.Equals(request.ServerToClientClipboard, RequiredClipboardMode, StringComparison.Ordinal))
        {
            return CommandValidationResult.Failure($"serverToClientClipboard must be '{RequiredClipboardMode}'.");
        }

        if (request.DriveRedirection != false)
        {
            return CommandValidationResult.Failure("driveRedirection must be false.");
        }

        return ValidateTimeout(request, options);
    }

    private static CommandValidationResult ValidateTimeout(CommandRequest request, CommandValidationOptions options)
    {
        var timeout = request.TimeoutSeconds ?? options.DefaultTimeoutSeconds;

        if (timeout < options.MinTimeoutSeconds || timeout > options.MaxTimeoutSeconds)
        {
            return CommandValidationResult.Failure(
                $"TimeoutSeconds must be between {options.MinTimeoutSeconds} and {options.MaxTimeoutSeconds} (got {timeout}).");
        }

        return CommandValidationResult.Success(timeout);
    }
}
