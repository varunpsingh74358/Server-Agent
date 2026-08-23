using CloudOrc.Agent.Contracts.Enrollment;

namespace CloudOrc.ControlAgent.Tests.Enrollment;

public class EnrollmentTokenTests
{
    [Fact]
    public void EncodeThenDecode_RoundTripsUrlAndSecret()
    {
        var token = EnrollmentToken.Encode("https://enroll.example.test/api/enroll", "s3cret-value");

        var decoded = EnrollmentToken.TryDecode(token, out var payload);

        Assert.True(decoded);
        Assert.Equal("https://enroll.example.test/api/enroll", payload!.EnrollmentUrl);
        Assert.Equal("s3cret-value", payload.Secret);
    }

    [Fact]
    public void Encode_ProducesAnOpaqueTokenWithTheDocumentedPrefix()
    {
        var token = EnrollmentToken.Encode("https://enroll.example.test/api/enroll", "s3cret-value");

        Assert.StartsWith("ENR-", token);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-token-at-all")]
    [InlineData("ENR-")]
    [InlineData("ENR-not-valid-base64!!!")]
    public void TryDecode_MalformedInput_ReturnsFalseWithoutThrowing(string? malformed)
    {
        var result = EnrollmentToken.TryDecode(malformed, out var payload);

        Assert.False(result);
        Assert.Null(payload);
    }

    [Fact]
    public void TryDecode_ValidBase64ButNotJson_ReturnsFalse()
    {
        var garbageJson = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("not json"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var result = EnrollmentToken.TryDecode($"ENR-{garbageJson}", out var payload);

        Assert.False(result);
        Assert.Null(payload);
    }

    [Fact]
    public void TryDecode_PayloadWithNonAbsoluteUrl_ReturnsFalse()
    {
        var token = EnrollmentToken.Encode("not-a-real-url", "secret");

        var result = EnrollmentToken.TryDecode(token, out var payload);

        Assert.False(result);
        Assert.Null(payload);
    }

    [Fact]
    public void Encode_RejectsEmptyEnrollmentUrl()
    {
        Assert.Throws<ArgumentException>(() => EnrollmentToken.Encode("", "secret"));
    }

    [Fact]
    public void Encode_RejectsEmptySecret()
    {
        Assert.Throws<ArgumentException>(() => EnrollmentToken.Encode("https://enroll.example.test/api/enroll", ""));
    }
}
