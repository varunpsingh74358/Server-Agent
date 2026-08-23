using CloudOrc.Agent.Contracts.Commands;

namespace CloudOrc.ControlAgent.Tests.Commands;

public class CommandRequestValidatorTests
{
    private static readonly CommandValidationOptions DefaultOptions = new()
    {
        MinTimeoutSeconds = 1,
        MaxTimeoutSeconds = 3600,
        DefaultTimeoutSeconds = 30,
        MaxScriptLength = 32_000
    };

    [Fact]
    public void Validate_NullRequest_ReturnsFailure()
    {
        var result = CommandRequestValidator.Validate(null, DefaultOptions);

        Assert.False(result.IsValid);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Validate_EmptyCommandId_IsRejected()
    {
        var request = new CommandRequest { CommandId = "", Script = "Get-Date" };

        var result = CommandRequestValidator.Validate(request, DefaultOptions);

        Assert.False(result.IsValid);
        Assert.Contains("CommandId", result.Error);
    }

    [Fact]
    public void Validate_WhitespaceCommandId_IsRejected()
    {
        var request = new CommandRequest { CommandId = "   ", Script = "Get-Date" };

        var result = CommandRequestValidator.Validate(request, DefaultOptions);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_CommandIdWithInvalidFileNameCharacters_IsRejected()
    {
        var request = new CommandRequest { CommandId = "bad/id:name", Script = "Get-Date" };

        var result = CommandRequestValidator.Validate(request, DefaultOptions);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_EmptyScript_IsRejected()
    {
        var request = new CommandRequest { CommandId = "test-001", Script = "" };

        var result = CommandRequestValidator.Validate(request, DefaultOptions);

        Assert.False(result.IsValid);
        Assert.Contains("Script", result.Error);
    }

    [Fact]
    public void Validate_ScriptExceedingMaxLength_IsRejected()
    {
        var request = new CommandRequest { CommandId = "test-001", Script = new string('a', 100), TimeoutSeconds = 10 };
        var options = new CommandValidationOptions { MaxScriptLength = 50, MinTimeoutSeconds = 1, MaxTimeoutSeconds = 3600, DefaultTimeoutSeconds = 30 };

        var result = CommandRequestValidator.Validate(request, options);

        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(3601)]
    public void Validate_TimeoutOutsideAllowedRange_IsRejected(int timeoutSeconds)
    {
        var request = new CommandRequest { CommandId = "test-001", Script = "Get-Date", TimeoutSeconds = timeoutSeconds };

        var result = CommandRequestValidator.Validate(request, DefaultOptions);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_MissingTimeout_FallsBackToConfiguredDefault()
    {
        var request = new CommandRequest { CommandId = "test-001", Script = "Get-Date", TimeoutSeconds = null };

        var result = CommandRequestValidator.Validate(request, DefaultOptions);

        Assert.True(result.IsValid);
        Assert.Equal(DefaultOptions.DefaultTimeoutSeconds, result.EffectiveTimeoutSeconds);
    }

    [Fact]
    public void Validate_ValidRequest_Succeeds()
    {
        var request = new CommandRequest { CommandId = "test-001", Script = "Get-Date", TimeoutSeconds = 30 };

        var result = CommandRequestValidator.Validate(request, DefaultOptions);

        Assert.True(result.IsValid);
        Assert.Equal(30, result.EffectiveTimeoutSeconds);
        Assert.Null(result.Error);
    }
}
