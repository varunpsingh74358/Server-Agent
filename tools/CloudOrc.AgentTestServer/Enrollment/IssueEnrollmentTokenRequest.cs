namespace CloudOrc.AgentTestServer.Enrollment;

/// <summary>Body for <c>POST /api/enrollment-tokens</c> - generates a new test enrollment token.</summary>
public sealed class IssueEnrollmentTokenRequest
{
    public int? ValidForSeconds { get; set; }

    /// <summary>
    /// Optional. The IP address of the specific server this token is being issued for
    /// (the one an admin chose when adding the server). When set, <c>POST /api/enroll</c>
    /// rejects redemption from any other IP - binding the token to the machine it was
    /// meant for, not just to whoever happens to hold the token string. Omit to preserve
    /// the previous, unbound behavior (any machine holding the token can redeem it).
    /// </summary>
    public string? ExpectedIpAddress { get; set; }
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
