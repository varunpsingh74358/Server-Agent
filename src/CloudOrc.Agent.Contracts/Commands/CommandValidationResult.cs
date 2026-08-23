namespace CloudOrc.Agent.Contracts.Commands;

public sealed class CommandValidationResult
{
    private CommandValidationResult(bool isValid, string? error, int effectiveTimeoutSeconds)
    {
        IsValid = isValid;
        Error = error;
        EffectiveTimeoutSeconds = effectiveTimeoutSeconds;
    }

    public bool IsValid { get; }

    public string? Error { get; }

    /// <summary>
    /// The timeout to actually use: the request's value when valid, otherwise 0 when invalid.
    /// </summary>
    public int EffectiveTimeoutSeconds { get; }

    public static CommandValidationResult Success(int effectiveTimeoutSeconds) =>
        new(true, null, effectiveTimeoutSeconds);

    public static CommandValidationResult Failure(string error) =>
        new(false, error, 0);
}
