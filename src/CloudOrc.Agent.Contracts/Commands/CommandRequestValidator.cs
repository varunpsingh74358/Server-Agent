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

        if (string.IsNullOrWhiteSpace(request.Script))
        {
            return CommandValidationResult.Failure("Script is required and cannot be empty.");
        }

        if (request.Script.Length > options.MaxScriptLength)
        {
            return CommandValidationResult.Failure($"Script exceeds the maximum allowed length of {options.MaxScriptLength} characters.");
        }

        var timeout = request.TimeoutSeconds ?? options.DefaultTimeoutSeconds;

        if (timeout < options.MinTimeoutSeconds || timeout > options.MaxTimeoutSeconds)
        {
            return CommandValidationResult.Failure(
                $"TimeoutSeconds must be between {options.MinTimeoutSeconds} and {options.MaxTimeoutSeconds} (got {timeout}).");
        }

        return CommandValidationResult.Success(timeout);
    }
}
