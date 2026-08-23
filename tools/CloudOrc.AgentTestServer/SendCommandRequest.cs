namespace CloudOrc.AgentTestServer;

/// <summary>Request body for the scriptable POST /send endpoint.</summary>
public sealed class SendCommandRequest
{
    public string? CommandId { get; init; }

    public required string Script { get; init; }

    public int? TimeoutSeconds { get; init; }
}
