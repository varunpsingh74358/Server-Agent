using CloudOrc.Agent.Contracts.Commands;
using CloudOrc.ControlAgent.Backend;
using CloudOrc.ControlAgent.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudOrc.ControlAgent.Tests.Backend;

public sealed class WssCommandSourceTests : IDisposable
{
    private readonly string _root;
    private readonly ControlAgentOptions _options;

    public WssCommandSourceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "CloudOrcTests", Guid.NewGuid().ToString("N"));
        _options = new ControlAgentOptions { DataDirectory = _root };

        Directory.CreateDirectory(_options.ResultsDirectory);
        Directory.CreateDirectory(_options.CompletedDirectory);
        Directory.CreateDirectory(_options.FailedDirectory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private WssCommandSource CreateSource() => new(Options.Create(_options), NullLogger<WssCommandSource>.Instance);

    [Fact]
    public void TryAcceptIncomingCommand_ValidCommand_IsAcceptedAndEnqueued()
    {
        var source = CreateSource();
        var request = new CommandRequest { CommandId = "wss-001", Script = "Get-Date", TimeoutSeconds = 30 };

        var accepted = source.TryAcceptIncomingCommand(request, out var reason);

        Assert.True(accepted);
        Assert.Null(reason);
    }

    [Fact]
    public void TryAcceptIncomingCommand_InvalidCommand_IsRejectedWithReason()
    {
        var source = CreateSource();
        var request = new CommandRequest { CommandId = "", Script = "Get-Date" };

        var accepted = source.TryAcceptIncomingCommand(request, out var reason);

        Assert.False(accepted);
        Assert.NotNull(reason);
    }

    [Fact]
    public void TryAcceptIncomingCommand_DuplicateWithinSameSession_IsRejectedOnSecondAttempt()
    {
        var source = CreateSource();
        var request = new CommandRequest { CommandId = "wss-002", Script = "Get-Date", TimeoutSeconds = 30 };

        var first = source.TryAcceptIncomingCommand(request, out _);
        var second = source.TryAcceptIncomingCommand(request, out var secondReason);

        Assert.True(first);
        Assert.False(second);
        Assert.Contains("already", secondReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryAcceptIncomingCommand_CommandIdWithExistingResultFile_IsRejected()
    {
        // Simulates the cross-source case: a command with this id already completed,
        // e.g. via the local file source, before this WSS command arrived.
        File.WriteAllText(Path.Combine(_options.ResultsDirectory, "wss-003.result.json"), "{}");
        var source = CreateSource();
        var request = new CommandRequest { CommandId = "wss-003", Script = "Get-Date", TimeoutSeconds = 30 };

        var accepted = source.TryAcceptIncomingCommand(request, out var reason);

        Assert.False(accepted);
        Assert.NotNull(reason);
    }

    [Fact]
    public async Task GetCommandsAsync_YieldsAcceptedJobWithCorrectOriginSource()
    {
        var source = CreateSource();
        var request = new CommandRequest { CommandId = "wss-004", Script = "Get-Date", TimeoutSeconds = 30 };
        source.TryAcceptIncomingCommand(request, out _);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var job in source.GetCommandsAsync(cts.Token))
        {
            Assert.Equal("wss-004", job.Request.CommandId);
            Assert.Same(source, job.OriginSource);
            return;
        }

        Assert.Fail("Expected to receive the accepted job.");
    }

    [Fact]
    public async Task AcknowledgeAsync_DoesNotThrow_AndPerformsNoFileOperations()
    {
        var source = CreateSource();
        var request = new CommandRequest { CommandId = "wss-005", Script = "Get-Date", TimeoutSeconds = 30 };
        source.TryAcceptIncomingCommand(request, out _);

        CommandJob? job = null;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var j in source.GetCommandsAsync(cts.Token))
        {
            job = j;
            break;
        }

        Assert.NotNull(job);
        await source.AcknowledgeAsync(job!, succeeded: true, CancellationToken.None);
        // No exception, and no completed\/failed\ file should appear for a WSS-origin job.
        Assert.Empty(Directory.EnumerateFiles(_options.CompletedDirectory));
        Assert.Empty(Directory.EnumerateFiles(_options.FailedDirectory));
    }
}
