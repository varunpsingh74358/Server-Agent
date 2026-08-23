namespace CloudOrc.Agent.Contracts.Commands;

/// <summary>
/// Configurable limits used to validate an incoming <see cref="CommandRequest"/>.
/// Bound from configuration (see ControlAgentOptions) so limits are never hardcoded
/// inside the validation logic itself.
/// </summary>
public sealed class CommandValidationOptions
{
    public int MinTimeoutSeconds { get; set; } = 1;

    public int MaxTimeoutSeconds { get; set; } = 3600;

    public int DefaultTimeoutSeconds { get; set; } = 30;

    public int MaxScriptLength { get; set; } = 32_000;
}
