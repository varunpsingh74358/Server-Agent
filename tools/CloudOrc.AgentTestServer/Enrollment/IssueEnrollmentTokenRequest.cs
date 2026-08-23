namespace CloudOrc.AgentTestServer.Enrollment;

/// <summary>Body for <c>POST /api/enrollment-tokens</c> - generates a new test enrollment token.</summary>
public sealed class IssueEnrollmentTokenRequest
{
    public int? ValidForSeconds { get; set; }
}

/// <summary>Body for <c>POST /api/enrollment-tokens/revoke</c>.</summary>
public sealed class RevokeEnrollmentTokenRequest
{
    public required string Token { get; set; }
}

/// <summary>Body for <c>POST /api/credentials/revoke</c> - simulates "revoked Agent cannot authenticate".</summary>
public sealed class RevokeCredentialRequest
{
    public required string Credential { get; set; }
}
