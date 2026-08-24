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

    [Fact]
    public async Task ExecuteAsync_WriteOutput_ReturnsSuccessWithExpectedText()
    {
        var executor = CreateExecutor();

        var outcome = await executor.ExecuteAsync("Write-Output 'hello-cloudorc'", TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(CommandStatus.Success, outcome.Status);
        Assert.Contains("hello-cloudorc", outcome.Output);
    }

    [Fact]
    public async Task ExecuteAsync_GetDate_ReturnsSuccess()
    {
        var executor = CreateExecutor();

        var outcome = await executor.ExecuteAsync("Get-Date", TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(CommandStatus.Success, outcome.Status);
        Assert.Single(outcome.Output);
    }

    [Fact]
    public async Task ExecuteAsync_GetComputerInfo_ReturnsSuccess()
    {
        var executor = CreateExecutor();

        var outcome = await executor.ExecuteAsync(
            "Get-ComputerInfo | Select-Object -Property CsName",
            TimeSpan.FromSeconds(60),
            CancellationToken.None);

        Assert.Equal(CommandStatus.Success, outcome.Status);
        Assert.NotEmpty(outcome.Output);
    }

    [Fact]
    public async Task ExecuteAsync_GetService_ReturnsSuccess()
    {
        var executor = CreateExecutor();

        var outcome = await executor.ExecuteAsync(
            "Get-Service | Select-Object -First 5",
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        Assert.Equal(CommandStatus.Success, outcome.Status);
        Assert.NotEmpty(outcome.Output);
    }

    [Fact]
    public async Task ExecuteAsync_GetProcess_ReturnsSuccess()
    {
        var executor = CreateExecutor();

        var outcome = await executor.ExecuteAsync(
            "Get-Process | Select-Object -First 5",
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        Assert.Equal(CommandStatus.Success, outcome.Status);
        Assert.NotEmpty(outcome.Output);
    }

    [Fact]
    public async Task ExecuteAsync_CreateTempFolder_ReturnsSuccessAndFolderExists()
    {
        var executor = CreateExecutor();
        var folder = Path.Combine(Path.GetTempPath(), $"cloudorc-test-{Guid.NewGuid():N}");

        try
        {
            var outcome = await executor.ExecuteAsync(
                $"New-Item -ItemType Directory -Path '{folder}' | Out-Null",
                TimeSpan.FromSeconds(30),
                CancellationToken.None);

            Assert.Equal(CommandStatus.Success, outcome.Status);
            Assert.True(Directory.Exists(folder));
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_CreateTempFile_ReturnsSuccessAndFileExists()
    {
        var executor = CreateExecutor();
        var file = Path.Combine(Path.GetTempPath(), $"cloudorc-test-{Guid.NewGuid():N}.txt");

        try
        {
            var outcome = await executor.ExecuteAsync(
                $"New-Item -ItemType File -Path '{file}' | Out-Null",
                TimeSpan.FromSeconds(30),
                CancellationToken.None);

            Assert.Equal(CommandStatus.Success, outcome.Status);
            Assert.True(File.Exists(file));
        }
        finally
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_ReadTempFile_ReturnsSuccessWithContent()
    {
        var executor = CreateExecutor();
        var file = Path.Combine(Path.GetTempPath(), $"cloudorc-test-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(file, "original-content");

        try
        {
            var outcome = await executor.ExecuteAsync(
                $"Get-Content -Path '{file}'",
                TimeSpan.FromSeconds(30),
                CancellationToken.None);

            Assert.Equal(CommandStatus.Success, outcome.Status);
            Assert.Contains("original-content", outcome.Output);
        }
        finally
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_ModifyTempFile_UpdatesFileContentOnDisk()
    {
        var executor = CreateExecutor();
        var file = Path.Combine(Path.GetTempPath(), $"cloudorc-test-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(file, "original-content");

        try
        {
            var outcome = await executor.ExecuteAsync(
                $"Set-Content -Path '{file}' -Value 'updated-content'",
                TimeSpan.FromSeconds(30),
                CancellationToken.None);

            Assert.Equal(CommandStatus.Success, outcome.Status);
            Assert.Equal("updated-content", (await File.ReadAllTextAsync(file)).Trim());
        }
        finally
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_DeleteTempFile_RemovesFileFromDisk()
    {
        var executor = CreateExecutor();
        var file = Path.Combine(Path.GetTempPath(), $"cloudorc-test-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(file, "to-be-deleted");

        var outcome = await executor.ExecuteAsync(
            $"Remove-Item -Path '{file}' -Force",
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        Assert.Equal(CommandStatus.Success, outcome.Status);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task ExecuteAsync_IntentionalFailure_ReturnsFailedStatusWithRealError()
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
    public async Task ExecuteAsync_LongRunningScript_CompletesSuccessfullyWithinGenerousTimeout()
    {
        var executor = CreateExecutor();

        var outcome = await executor.ExecuteAsync(
            "Start-Sleep -Seconds 3; Write-Output 'done'",
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        Assert.Equal(CommandStatus.Success, outcome.Status);
        Assert.True(outcome.DurationMilliseconds >= 2900, "The script should have actually run for ~3 seconds, not been short-circuited.");
        Assert.Contains("done", outcome.Output);
    }

    [Fact]
    public async Task ExecuteAsync_LargeMultilineScript_ExecutesInFullWithoutTruncation()
    {
        var executor = CreateExecutor();
        const int lineCount = 20_000; // generates a script comfortably over 500KB
        var script = string.Join(Environment.NewLine, Enumerable.Range(0, lineCount).Select(i => $"Write-Output 'line-{i}'"));
        Assert.True(script.Length > 500_000, "Test setup should generate a script over 500KB.");

        var outcome = await executor.ExecuteAsync(script, TimeSpan.FromSeconds(60), CancellationToken.None);

        Assert.Equal(CommandStatus.Success, outcome.Status);
        Assert.Equal(lineCount, outcome.Output.Count);
        Assert.Equal("line-0", outcome.Output[0]);
        Assert.Equal($"line-{lineCount - 1}", outcome.Output[^1]);
    }

    [Fact]
    public async Task ExecuteAsync_UnicodeScript_PreservesNonAsciiCharacters()
    {
        var executor = CreateExecutor();
        const string unicodeText = "héllo wörld — 你好 — こんにちは — 🚀";

        var outcome = await executor.ExecuteAsync($"Write-Output '{unicodeText}'", TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(CommandStatus.Success, outcome.Status);
        Assert.Contains(unicodeText, outcome.Output);
    }

    [Fact]
    public async Task ExecuteAsync_ScriptCallingExit_CapturesExitCode()
    {
        var executor = CreateExecutor();

        var outcome = await executor.ExecuteAsync("exit 7", TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(7, outcome.ExitCode);
    }

    [Fact]
    public async Task ExecuteAsync_ScriptWithoutExitCall_HasNullExitCode()
    {
        var executor = CreateExecutor();

        var outcome = await executor.ExecuteAsync("Write-Output 'no exit call'", TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Null(outcome.ExitCode);
    }
}
