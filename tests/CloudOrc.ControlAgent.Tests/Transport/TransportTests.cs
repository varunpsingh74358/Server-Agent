using System.Net;
using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.ControlAgent.Backend;
using CloudOrc.ControlAgent.Configuration;
using CloudOrc.ControlAgent.Transport;

namespace CloudOrc.ControlAgent.Tests.Transport;

public class ConnectionRetryPolicyTests
{
    [Fact]
    public void FirstUnauthorized_TriesSessionRenewalImmediately_WhenPossible()
    {
        var decision = new ConnectionRetryPolicy().OnFailure(HttpStatusCode.Unauthorized, null, renewalAvailable: true);
        Assert.True(decision.AttemptSessionRenewal);
        Assert.Equal(TimeSpan.Zero, decision.Delay);
    }

    [Fact]
    public void RepeatedUnauthorized_BecomesExpired_WithSlowRetries()
    {
        var policy = new ConnectionRetryPolicy();
        ReconnectDecision last = null!;
        for (var i = 0; i < ConnectionRetryPolicy.UnauthorizedBeforeExpired; i++)
        {
            last = policy.OnFailure(HttpStatusCode.Unauthorized, null, renewalAvailable: false);
        }

        Assert.Equal(AuthorizationHealth.Expired, last.Authorization);
        Assert.Equal(ConnectionRetryPolicy.ExpiredRetryDelay, last.Delay);
        Assert.False(last.StopReconnecting);
    }

    [Fact]
    public void Forbidden_IsRevoked_AndIsNeverRetriedIndefinitely()
    {
        var policy = new ConnectionRetryPolicy();
        var first = policy.OnFailure(HttpStatusCode.Forbidden, null, true);
        Assert.Equal(AuthorizationHealth.Revoked, first.Authorization);
        Assert.Equal(ConnectionRetryPolicy.RevokedRetryDelay, first.Delay);
        Assert.False(first.StopReconnecting);

        policy.OnFailure(HttpStatusCode.Forbidden, null, true);
        var third = policy.OnFailure(HttpStatusCode.Forbidden, null, true);
        Assert.True(third.StopReconnecting);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public void Overload_HonoursRetryAfter(HttpStatusCode status)
    {
        var decision = new ConnectionRetryPolicy().OnFailure(status, TimeSpan.FromSeconds(90), false);
        Assert.Equal(TimeSpan.FromSeconds(90), decision.Delay);
        Assert.False(decision.StopReconnecting);
    }

    [Fact]
    public void SuccessfulConnect_ResetsAuthorizationHealth()
    {
        var policy = new ConnectionRetryPolicy();
        policy.OnFailure(HttpStatusCode.Forbidden, null, false);
        policy.OnConnected();
        Assert.Equal(AuthorizationHealth.Ok, policy.Authorization);
    }
}

public class RetryAfterTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DeltaSeconds() => Assert.Equal(TimeSpan.FromSeconds(120), RetryAfter.Parse("120", Now));

    [Fact]
    public void HttpDate() => Assert.Equal(TimeSpan.FromMinutes(5), RetryAfter.Parse(Now.AddMinutes(5).ToString("r"), Now));

    [Fact]
    public void IsBoundedToOneHour() => Assert.Equal(RetryAfter.Maximum, RetryAfter.Parse("999999", Now));

    [Theory]
    [InlineData(null)]
    [InlineData("soon")]
    [InlineData("-5")]
    public void InvalidValuesAreIgnored(string? value) => Assert.Null(RetryAfter.Parse(value, Now));
}

public class ControlPlaneAddressTests
{
    [Fact]
    public void DerivedFromSecureWebSocketUrl() =>
        Assert.Equal("https://agents.example.test:8443/", ControlPlaneAddress.Resolve(null, null, "wss://agents.example.test:8443/agent", false)!.ToString());

    [Fact]
    public void EnrollmentValueWinsOverDerivation_AndOverrideWinsOverBoth()
    {
        Assert.Equal("https://api.example.test/", ControlPlaneAddress.Resolve(null, "https://api.example.test", "wss://other.example.test/agent", false)!.ToString());
        Assert.Equal("https://override.example.test/", ControlPlaneAddress.Resolve("https://override.example.test/", "https://api.example.test", null, false)!.ToString());
    }

    [Fact]
    public void PlainHttp_RequiresTheDevelopmentFlag()
    {
        Assert.Null(ControlPlaneAddress.Resolve(null, null, "ws://localhost:5299/agent", false));
        Assert.Equal("http://localhost:5299/", ControlPlaneAddress.Resolve(null, null, "ws://localhost:5299/agent", true)!.ToString());
    }
}

public class FullJitterBackoffTests
{
    [Fact]
    public void FullJitter_StaysWithinOneSecondAndTheExponentialCap()
    {
        var options = new BackendConnectionOptions { ReconnectInitialDelaySeconds = 2, ReconnectMaximumDelaySeconds = 60, ReconnectFullJitter = true };
        var calculator = new ReconnectBackoffCalculator(options);
        var caps = new[] { 2, 4, 8, 16, 32, 60, 60 };
        foreach (var cap in caps)
        {
            var delay = calculator.NextDelay();
            Assert.InRange(delay.TotalSeconds, 1, cap);
        }
    }
}

public class CredentialRenewalSchedulerTests
{
    [Fact]
    public void RenewsOnlyInsideTheLeadWindow()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.False(CredentialRenewalScheduler.ShouldRenew(null, now));
        Assert.False(CredentialRenewalScheduler.ShouldRenew(now.AddHours(2), now));
        Assert.True(CredentialRenewalScheduler.ShouldRenew(now.AddMinutes(10), now));
    }
}

public class SecretRedactorTests
{
    [Theory]
    [InlineData("Authorization: Bearer abc.def.ghi")]
    [InlineData("KOPIA_PASSWORD=hunter2hunter2")]
    [InlineData("{\"access_token\":\"ya29.a0AfH6SMBx\"}")]
    [InlineData("https://user:pa55word@storage.example.test/bucket")]
    [InlineData("key AKIAABCDEFGHIJKLMNOP used")]
    public void SecretsAreMasked(string text)
    {
        var redacted = SecretRedactor.Redact(text)!;
        Assert.Contains("[REDACTED]", redacted);
        Assert.DoesNotContain("hunter2", redacted);
        Assert.DoesNotContain("ya29.a0", redacted);
        Assert.DoesNotContain("pa55word", redacted);
        Assert.DoesNotContain("AKIAABCDEFGHIJKLMNOP", redacted);
    }

    [Fact]
    public void OrdinaryTextIsUntouched() =>
        Assert.Equal("Backup of D:\\Tally finished", SecretRedactor.Redact("Backup of D:\\Tally finished"));
}
