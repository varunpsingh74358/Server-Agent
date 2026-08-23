using CloudOrc.Agent.Contracts.Commands;
using CloudOrc.ControlAgent.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudOrc.ControlAgent.Tests.Services;

/// <summary>
/// Exercises the real PowerShell SDK through <see cref="PowerShellCommandExecutor"/>.
/// These run actual PowerShell pipelines rather than mocks, since the whole point of this
/// class is its integration with System.Management.Automation - a mock would not prove
/// timeout/cancellation/error-stream handling actually works.
/// </summary>
public class PowerShellCommandExecutorTests
{
    private static PowerShellCommandExecutor CreateExecutor() => new(NullLogger<PowerShellCommandExecutor>.Instance);

    [Fact]
    public async Task ExecuteAsync_SimpleCommand_ReturnsSuccessWithOutput()
    {
        var executor = CreateExecutor();

        var outcome = await executor.ExecuteAsync("Get-Date", TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(CommandStatus.Success, outcome.Status);
        Assert.Single(outcome.Output);
        Assert.Null(outcome.Error);
    }

    [Fact]
    public async Task ExecuteAsync_ScriptWithNonTerminatingError_ReturnsFailedWithErrorCaptured()
    {
        var executor = CreateExecutor();

        var outcome = await executor.ExecuteAsync(
            "Get-Service -Name \"DefinitelyDoesNotExist\"",
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        Assert.Equal(CommandStatus.Failed, outcome.Status);
        Assert.NotNull(outcome.Error);
    }

    [Fact]
    public async Task ExecuteAsync_ExceedingTimeout_ReturnsTimeoutAndStopsExecution()
    {
        var executor = CreateExecutor();

        var outcome = await executor.ExecuteAsync(
            "Start-Sleep -Seconds 30",
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.Equal(CommandStatus.Timeout, outcome.Status);
        Assert.True(outcome.DurationMilliseconds < 10_000, "Execution should have been stopped well before the sleep would have finished naturally.");
    }

    [Fact]
    public async Task ExecuteAsync_CompletingBeforeTimeout_ReturnsSuccess()
    {
        var executor = CreateExecutor();

        var outcome = await executor.ExecuteAsync("Start-Sleep -Milliseconds 200", TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(CommandStatus.Success, outcome.Status);
    }

    [Fact]
    public async Task ExecuteAsync_ExternalCancellation_ReturnsCancelled()
    {
        var executor = CreateExecutor();
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));

        var outcome = await executor.ExecuteAsync("Start-Sleep -Seconds 30", TimeSpan.FromSeconds(60), cts.Token);

        Assert.Equal(CommandStatus.Cancelled, outcome.Status);
    }

    [Fact]
    public async Task ExecuteAsync_FailedCommand_DoesNotThrow_AndSubsequentCommandStillSucceeds()
    {
        var executor = CreateExecutor();

        var failedOutcome = await executor.ExecuteAsync(
            "Get-Service -Name \"DefinitelyDoesNotExist\"",
            TimeSpan.FromSeconds(30),
            CancellationToken.None);
        Assert.Equal(CommandStatus.Failed, failedOutcome.Status);

        var nextOutcome = await executor.ExecuteAsync("Get-Date", TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Equal(CommandStatus.Success, nextOutcome.Status);
    }
}
