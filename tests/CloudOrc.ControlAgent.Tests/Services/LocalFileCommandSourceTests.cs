using System.Text.Json;
using CloudOrc.Agent.Contracts.Commands;
using CloudOrc.ControlAgent.Configuration;
using CloudOrc.ControlAgent.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudOrc.ControlAgent.Tests.Services;

/// <summary>
/// These tests exercise <see cref="LocalFileCommandSource"/> against a real, temporary
/// directory tree - they are small, fast file-system tests rather than mocked unit tests,
/// which is the only practical way to verify the actual move/rename/duplicate-protection
/// behavior the class is responsible for.
/// </summary>
public sealed class LocalFileCommandSourceTests : IDisposable
{
    private readonly string _root;
    private readonly ControlAgentOptions _options;

    public LocalFileCommandSourceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "CloudOrcTests", Guid.NewGuid().ToString("N"));
        _options = new ControlAgentOptions
        {
            DataDirectory = _root,
            PollIntervalSeconds = 1,
            FileStabilityMilliseconds = 0
        };

        Directory.CreateDirectory(_options.CommandsDirectory);
        Directory.CreateDirectory(_options.ProcessingDirectory);
        Directory.CreateDirectory(_options.CompletedDirectory);
        Directory.CreateDirectory(_options.FailedDirectory);
        Directory.CreateDirectory(_options.ResultsDirectory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup only.
        }
    }

    private LocalFileCommandSource CreateSource() =>
        new(Options.Create(_options), NullLogger<LocalFileCommandSource>.Instance);

    private void WriteCommandFile(string fileName, object payload)
    {
        var path = Path.Combine(_options.CommandsDirectory, fileName);
        File.WriteAllText(path, JsonSerializer.Serialize(payload));
    }

    private static async Task<CommandJob> GetFirstJobAsync(LocalFileCommandSource source, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        await foreach (var job in source.GetCommandsAsync(cts.Token))
        {
            cts.Cancel();
            return job;
        }

        throw new TimeoutException("No command job was produced within the timeout.");
    }

    [Fact]
    public async Task ValidCommand_IsClaimedAndMovedToProcessing()
    {
        WriteCommandFile("test-001.json", new { commandId = "test-001", script = "Get-Date", timeoutSeconds = 30 });
        var source = CreateSource();

        var job = await GetFirstJobAsync(source, TimeSpan.FromSeconds(5));

        Assert.Equal("test-001", job.Request.CommandId);
        Assert.Equal(30, job.EffectiveTimeoutSeconds);
        Assert.False(File.Exists(Path.Combine(_options.CommandsDirectory, "test-001.json")));
        Assert.True(File.Exists(Path.Combine(_options.ProcessingDirectory, "test-001.json")));
    }

    [Fact]
    public async Task Acknowledge_Success_MovesFileToCompleted()
    {
        WriteCommandFile("test-001.json", new { commandId = "test-001", script = "Get-Date", timeoutSeconds = 30 });
        var source = CreateSource();
        var job = await GetFirstJobAsync(source, TimeSpan.FromSeconds(5));

        await source.AcknowledgeAsync(job, succeeded: true, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_options.CompletedDirectory, "test-001.json")));
        Assert.False(File.Exists(job.SourceReference));
    }

    [Fact]
    public async Task Acknowledge_Failure_MovesFileToFailed()
    {
        WriteCommandFile("test-002.json", new { commandId = "test-002", script = "Get-Date", timeoutSeconds = 30 });
        var source = CreateSource();
        var job = await GetFirstJobAsync(source, TimeSpan.FromSeconds(5));

        await source.AcknowledgeAsync(job, succeeded: false, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_options.FailedDirectory, "test-002.json")));
    }

    [Fact]
    public async Task InvalidJson_IsMovedToFailedAndNeverYielded()
    {
        File.WriteAllText(Path.Combine(_options.CommandsDirectory, "broken.json"), "{ this is not valid json");
        var source = CreateSource();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var sawAnyJob = false;
        try
        {
            await foreach (var _ in source.GetCommandsAsync(cts.Token))
            {
                sawAnyJob = true;
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: the loop runs until the timeout because nothing valid ever arrives.
        }

        Assert.False(sawAnyJob);
        Assert.True(File.Exists(Path.Combine(_options.FailedDirectory, "broken.json")));
        Assert.False(File.Exists(Path.Combine(_options.CommandsDirectory, "broken.json")));
    }

    [Fact]
    public async Task InvalidCommandId_IsRejectedAndMovedToFailed()
    {
        WriteCommandFile("bad-id.json", new { commandId = "", script = "Get-Date", timeoutSeconds = 30 });
        var source = CreateSource();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var sawAnyJob = false;
        try
        {
            await foreach (var _ in source.GetCommandsAsync(cts.Token))
            {
                sawAnyJob = true;
            }
        }
        catch (OperationCanceledException)
        {
        }

        Assert.False(sawAnyJob);
        Assert.True(File.Exists(Path.Combine(_options.FailedDirectory, "bad-id.json")));
    }

    [Fact]
    public async Task DuplicateCommandId_WithExistingResult_IsSkipped()
    {
        // Simulate a command that already produced a result in a previous run.
        File.WriteAllText(
            Path.Combine(_options.ResultsDirectory, "test-001.result.json"),
            JsonSerializer.Serialize(new { commandId = "test-001", status = "Success" }));

        WriteCommandFile("test-001.json", new { commandId = "test-001", script = "Get-Date", timeoutSeconds = 30 });
        var source = CreateSource();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var sawAnyJob = false;
        try
        {
            await foreach (var _ in source.GetCommandsAsync(cts.Token))
            {
                sawAnyJob = true;
            }
        }
        catch (OperationCanceledException)
        {
        }

        Assert.False(sawAnyJob);
        Assert.False(File.Exists(Path.Combine(_options.ProcessingDirectory, "test-001.json")));
    }

    [Fact]
    public async Task DuplicateCommandId_ClaimedInSameSession_IsSkippedOnSecondFile()
    {
        WriteCommandFile("first.json", new { commandId = "dup-1", script = "Get-Date", timeoutSeconds = 30 });
        var source = CreateSource();
        var firstJob = await GetFirstJobAsync(source, TimeSpan.FromSeconds(5));
        Assert.Equal("dup-1", firstJob.Request.CommandId);

        // A second file arrives with the same CommandId before the first has produced a result.
        WriteCommandFile("second.json", new { commandId = "dup-1", script = "Get-Process", timeoutSeconds = 30 });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var sawAnyJob = false;
        try
        {
            await foreach (var _ in source.GetCommandsAsync(cts.Token))
            {
                sawAnyJob = true;
            }
        }
        catch (OperationCanceledException)
        {
        }

        Assert.False(sawAnyJob);
    }

    [Fact]
    public async Task OrphanedProcessingFile_IsRecoveredAndReprocessedOnStartup()
    {
        // Simulate a crash: a file was claimed (moved to processing\) but never finished.
        File.WriteAllText(
            Path.Combine(_options.ProcessingDirectory, "orphan-001.json"),
            JsonSerializer.Serialize(new { commandId = "orphan-001", script = "Get-Date", timeoutSeconds = 30 }));

        var source = CreateSource();
        var job = await GetFirstJobAsync(source, TimeSpan.FromSeconds(5));

        Assert.Equal("orphan-001", job.Request.CommandId);
    }
}
