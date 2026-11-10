using CloudOrc.Agent.Tray;

namespace CloudOrc.Agent.Tray.Tests;

public sealed class ActivationCodeFormatTests
{
    [Theory]
    [InlineData("abcd-1234", "ABCD-1234")]
    [InlineData("  ab cd 12 ", "ABCD12")]
    [InlineData("AbC\tdEf", "ABCDEF")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Normalize_UppercasesAndStripsWhitespace(string? input, string expected) =>
        Assert.Equal(expected, ActivationCodeFormat.Normalize(input));

    [Theory]
    [InlineData("ABCDEF")]       // 6
    [InlineData("ABCD1234")]     // 8
    [InlineData("ABC-DEF")]      // 6 + hyphen
    [InlineData("ABCD-1234")]    // 8 + hyphen
    [InlineData("A-BCDEFG")]
    [InlineData("123456")]
    public void IsValid_AcceptsSixToEightAlphanumericsWithOptionalSingleHyphen(string code) =>
        Assert.True(ActivationCodeFormat.IsValid(code));

    [Theory]
    [InlineData("ABCDE")]          // 5
    [InlineData("ABCD12345")]      // 9
    [InlineData("AB-CD-EF")]       // two hyphens
    [InlineData("-ABCDEF")]        // leading hyphen
    [InlineData("ABCDEF-")]        // trailing hyphen
    [InlineData("ABC_DEF")]        // underscore
    [InlineData("ABCDÉF")]         // non-ASCII letter
    [InlineData("abcdef")]         // not normalized (lowercase)
    [InlineData("ABC DEF")]        // not normalized (space)
    [InlineData("")]
    [InlineData(null)]
    public void IsValid_RejectsMalformedCodes(string? code) =>
        Assert.False(ActivationCodeFormat.IsValid(code));

    [Fact]
    public void TryNormalize_ReturnsNormalizedCodeWhenValid()
    {
        Assert.True(ActivationCodeFormat.TryNormalize(" abcd-1234 ", out var normalized));
        Assert.Equal("ABCD-1234", normalized);
    }

    [Fact]
    public void TryNormalize_FailsForInvalidCode()
    {
        Assert.False(ActivationCodeFormat.TryNormalize("ab", out var normalized));
        Assert.Equal("AB", normalized);
    }
}
