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

    private static CommandRequest ValidRdpTransferRequest(
        string? clientToServerClipboard = "PLAIN_TEXT_ONLY",
        string? serverToClientClipboard = "PLAIN_TEXT_ONLY",
        bool? driveRedirection = false) => new()
    {
        CommandId = "rdp-001",
        CommandType = CommandTypes.RdpTransferPolicyApply,
        ClientToServerClipboard = clientToServerClipboard,
        ServerToClientClipboard = serverToClientClipboard,
        DriveRedirection = driveRedirection,
        TimeoutSeconds = 30
    };

    [Fact]
    public void Validate_RdpTransferPolicyApply_ValidDesiredState_Succeeds()
    {
        var result = CommandRequestValidator.Validate(ValidRdpTransferRequest(), DefaultOptions);

        Assert.True(result.IsValid);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Validate_RdpTransferPolicyApply_DoesNotRequireScript()
    {
        var request = ValidRdpTransferRequest();
        Assert.Equal(string.Empty, request.Script);

        var result = CommandRequestValidator.Validate(request, DefaultOptions);

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("UNRESTRICTED")]
    [InlineData("plain_text_only")]
    [InlineData(null)]
    public void Validate_RdpTransferPolicyApply_ClientToServerClipboardNotPlainTextOnly_IsRejected(string? value)
    {
        var request = ValidRdpTransferRequest(clientToServerClipboard: value);

        var result = CommandRequestValidator.Validate(request, DefaultOptions);

        Assert.False(result.IsValid);
        Assert.Contains("clientToServerClipboard", result.Error);
    }

    [Fact]
    public void Validate_RdpTransferPolicyApply_ServerToClientClipboardNotPlainTextOnly_IsRejected()
    {
        var request = ValidRdpTransferRequest(serverToClientClipboard: "PLAIN_TEXT_AND_IMAGES");

        var result = CommandRequestValidator.Validate(request, DefaultOptions);

        Assert.False(result.IsValid);
        Assert.Contains("serverToClientClipboard", result.Error);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public void Validate_RdpTransferPolicyApply_DriveRedirectionNotFalse_IsRejected(bool? value)
    {
        var request = ValidRdpTransferRequest(driveRedirection: value);

        var result = CommandRequestValidator.Validate(request, DefaultOptions);

        Assert.False(result.IsValid);
        Assert.Contains("driveRedirection", result.Error);
    }
}
