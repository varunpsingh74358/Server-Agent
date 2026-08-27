using System.Net;
using CloudOrc.AgentTestServer.Enrollment;

namespace CloudOrc.AgentTestServer.Tests;

public class EnrollmentTokenStoreTests
{
    private const string EnrollUrl = "http://localhost:5299/api/enroll";

    private static string DecodeSecret(string token)
    {
        Assert.True(CloudOrc.Agent.Contracts.Enrollment.EnrollmentToken.TryDecode(token, out var decoded));
        return decoded!.Secret;
    }

    [Fact]
    public void IssueToken_ThenValidate_Succeeds()
    {
        var store = new EnrollmentTokenStore();
        var token = store.IssueToken(EnrollUrl, TimeSpan.FromMinutes(15));

        var result = store.ValidateAndConsume(DecodeSecret(token));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void ValidateAndConsume_UnknownSecret_IsInvalid()
    {
        var store = new EnrollmentTokenStore();

        var result = store.ValidateAndConsume("never-issued-secret");

        Assert.False(result.IsValid);
    }

    [Fact]
    public void ValidateAndConsume_SameTokenTwice_SecondAttemptFails_SingleUseEnforced()
    {
        var store = new EnrollmentTokenStore();
        var token = store.IssueToken(EnrollUrl, TimeSpan.FromMinutes(15));
        var secret = DecodeSecret(token);

        var first = store.ValidateAndConsume(secret);
        var second = store.ValidateAndConsume(secret);

        Assert.True(first.IsValid);
        Assert.False(second.IsValid);
        Assert.Contains("already been used", second.Error);
    }

    [Fact]
    public void ValidateAndConsume_ExpiredToken_IsInvalid()
    {
        var store = new EnrollmentTokenStore();
        var token = store.IssueToken(EnrollUrl, TimeSpan.FromMilliseconds(1));
        Thread.Sleep(50);

        var result = store.ValidateAndConsume(DecodeSecret(token));

        Assert.False(result.IsValid);
        Assert.Contains("expired", result.Error);
    }

    [Fact]
    public void RevokeByToken_BeforeUse_PreventsLaterValidation()
    {
        var store = new EnrollmentTokenStore();
        var token = store.IssueToken(EnrollUrl, TimeSpan.FromMinutes(15));

        var revoked = store.RevokeByToken(token);
        var result = store.ValidateAndConsume(DecodeSecret(token));

        Assert.True(revoked);
        Assert.False(result.IsValid);
        Assert.Contains("revoked", result.Error);
    }

    [Fact]
    public void RevokeByToken_AlreadyUsedToken_ReturnsFalse()
    {
        var store = new EnrollmentTokenStore();
        var token = store.IssueToken(EnrollUrl, TimeSpan.FromMinutes(15));
        store.ValidateAndConsume(DecodeSecret(token));

        var revoked = store.RevokeByToken(token);

        Assert.False(revoked);
    }

    [Fact]
    public void RevokeByToken_UnknownToken_ReturnsFalse()
    {
        var store = new EnrollmentTokenStore();
        var neverIssuedToken = CloudOrc.Agent.Contracts.Enrollment.EnrollmentToken.Encode(EnrollUrl, "made-up-secret");

        var revoked = store.RevokeByToken(neverIssuedToken);

        Assert.False(revoked);
    }

    [Fact]
    public async Task ValidateAndConsume_ConcurrentAttemptsOnTheSameToken_ExactlyOneSucceeds()
    {
        var store = new EnrollmentTokenStore();
        var token = store.IssueToken(EnrollUrl, TimeSpan.FromMinutes(15));
        var secret = DecodeSecret(token);

        var tasks = Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => store.ValidateAndConsume(secret)))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(r => r.IsValid));
        Assert.Equal(19, results.Count(r => !r.IsValid));
    }

    [Fact]
    public void IssuedTokens_AreUniquePerCall()
    {
        var store = new EnrollmentTokenStore();

        var tokenA = store.IssueToken(EnrollUrl, TimeSpan.FromMinutes(15));
        var tokenB = store.IssueToken(EnrollUrl, TimeSpan.FromMinutes(15));

        Assert.NotEqual(tokenA, tokenB);
    }

    [Fact]
    public void ValidateAndConsume_NoExpectedIp_AnyCallerIpSucceeds_BackwardCompatible()
    {
        // A token issued without ExpectedIpAddress must behave exactly as before this
        // feature existed - unbound, redeemable from anywhere.
        var store = new EnrollmentTokenStore();
        var token = store.IssueToken(EnrollUrl, TimeSpan.FromMinutes(15));

        var result = store.ValidateAndConsume(DecodeSecret(token), IPAddress.Parse("203.0.113.9"));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void ValidateAndConsume_ExpectedIp_MatchingCallerIp_Succeeds()
    {
        var store = new EnrollmentTokenStore();
        var expectedIp = IPAddress.Parse("10.20.30.40");
        var token = store.IssueToken(EnrollUrl, TimeSpan.FromMinutes(15), expectedIp);

        var result = store.ValidateAndConsume(DecodeSecret(token), IPAddress.Parse("10.20.30.40"));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void ValidateAndConsume_ExpectedIp_DifferentCallerIp_IsRejected_AndTokenStaysUnused()
    {
        var store = new EnrollmentTokenStore();
        var expectedIp = IPAddress.Parse("10.20.30.40");
        var token = store.IssueToken(EnrollUrl, TimeSpan.FromMinutes(15), expectedIp);
        var secret = DecodeSecret(token);

        var wrongIpAttempt = store.ValidateAndConsume(secret, IPAddress.Parse("10.20.30.41"));
        Assert.False(wrongIpAttempt.IsValid);
        Assert.Contains("bound to IP address", wrongIpAttempt.Error);

        // The mismatched attempt must not have consumed the token - the legitimate
        // server can still redeem it afterward.
        var correctIpAttempt = store.ValidateAndConsume(secret, expectedIp);
        Assert.True(correctIpAttempt.IsValid);
    }

    [Fact]
    public void ValidateAndConsume_ExpectedIp_NoCallerIpProvided_IsRejected()
    {
        var store = new EnrollmentTokenStore();
        var token = store.IssueToken(EnrollUrl, TimeSpan.FromMinutes(15), IPAddress.Parse("10.20.30.40"));

        var result = store.ValidateAndConsume(DecodeSecret(token));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void ValidateAndConsume_ExpectedIp_Ipv4MappedIpv6CallerAddress_StillMatches()
    {
        // Kestrel can report a loopback/dual-stack connection's RemoteIpAddress as the
        // IPv6-mapped form of an IPv4 address - this must not spuriously fail the check.
        var store = new EnrollmentTokenStore();
        var expectedIp = IPAddress.Parse("127.0.0.1");
        var token = store.IssueToken(EnrollUrl, TimeSpan.FromMinutes(15), expectedIp);

        var mappedCallerIp = IPAddress.Parse("::ffff:127.0.0.1");
        var result = store.ValidateAndConsume(DecodeSecret(token), mappedCallerIp);

        Assert.True(result.IsValid);
    }
}
