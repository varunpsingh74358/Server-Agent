using System.Text.Json;
using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.Agent.Contracts.Protocol;
using CloudOrc.ControlAgent.Backend;
using CloudOrc.ControlAgent.Commands;
using CloudOrc.ControlAgent.DurableQueue;
using CloudOrc.ControlAgent.Updates;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudOrc.ControlAgent.Tests.Commands;

public sealed class TypedCommandDispatcherTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cloudorc-dispatch-" + Guid.NewGuid().ToString("N"));
    private readonly AgentStateDatabase _db;
    private readonly OutgoingMessageChannel _outgoing = new();
    private readonly CountingHandler _handler = new();
    private readonly TypedCommandDispatcher _dispatcher;

    public TypedCommandDispatcherTests()
    {
        _db = new AgentStateDatabase(Path.Combine(_dir, "state.db"), NullLogger<AgentStateDatabase>.Instance, hardenDirectoryAcl: false);
        _dispatcher = new TypedCommandDispatcher([_handler], new CommandInboxStore(_db), _outgoing, TimeProvider.System, NullLogger<TypedCommandDispatcher>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void OnlyTypedCommands_AreRouted()
    {
        Assert.False(_dispatcher.TryRoute("{}", "powershell-exec"));
        Assert.False(_dispatcher.TryRoute("{}", null));
    }

    [Fact]
    public async Task RedeliveredCommand_ReplaysTheStoredResult_WithoutRunningTwice()
    {
        var json = Command("cmd-1", BackupCommandTypes.Cancel);

        await _dispatcher.DispatchAsync(json, BackupCommandTypes.Cancel);
        await _dispatcher.DispatchAsync(json, BackupCommandTypes.Cancel);

        Assert.Equal(1, _handler.Calls);
        var results = Drain().Where(m => m.Contains("COMMAND_RESULT", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, results.Count);
        Assert.Equal(results[0], results[1]); // byte-identical replay
    }

    [Fact]
    public async Task UnknownMajorVersion_IsRejected()
    {
        await _dispatcher.DispatchAsync(Command("cmd-2", BackupCommandTypes.Cancel, version: "2.0"), BackupCommandTypes.Cancel);
        Assert.Equal(0, _handler.Calls);
        var result = LastResult();
        Assert.Equal("Failed", result.GetProperty("status").GetString());
        Assert.Contains("UNSUPPORTED_COMMAND_VERSION", result.GetProperty("output")[0].GetString());
    }

    [Fact]
    public async Task TypedCommandWithoutAHandler_IsRejectedAsCapabilityNotAvailable()
    {
        await _dispatcher.DispatchAsync(Command("cmd-3", BackupCommandTypes.RestoreExecute), BackupCommandTypes.RestoreExecute);
        Assert.Contains("CAPABILITY_NOT_AVAILABLE", LastResult().GetProperty("output")[0].GetString());
    }

    [Fact]
    public async Task MalformedCommandId_IsRejectedWithAnErrorMessage()
    {
        await _dispatcher.DispatchAsync(Command("bad/id", BackupCommandTypes.Cancel), BackupCommandTypes.Cancel);
        Assert.Contains(Drain(), m => m.Contains("\"type\":\"ERROR\"", StringComparison.Ordinal));
    }

    private static string Command(string id, string type, string version = "1.0") =>
        JsonSerializer.Serialize(new { type = "COMMAND", commandId = id, correlationId = "corr", commandType = type, commandVersion = version, parameters = new { jobId = "job-1" } });

    private List<string> Drain()
    {
        var messages = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        try
        {
            var enumerator = _outgoing.ReadAllAsync(cts.Token).GetAsyncEnumerator(cts.Token);
            while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                messages.Add(enumerator.Current);
            }
        }
        catch (OperationCanceledException)
        {
        }

        return messages;
    }

    private JsonElement LastResult() =>
        JsonDocument.Parse(Drain().Last(m => m.Contains("COMMAND_RESULT", StringComparison.Ordinal))).RootElement.Clone();

    private sealed class CountingHandler : ITypedCommandHandler
    {
        public int Calls { get; private set; }

        public string CommandType => BackupCommandTypes.Cancel;

        public Task<TypedCommandAcceptance> HandleAsync(TypedCommandContext context, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new TypedCommandAcceptance { Accepted = true, JobId = context.Parameters<JobCancelParameters>()?.JobId });
        }
    }
}

public class CommandParameterValidationTests
{
    private static RestoreExecuteParameters Restore(string? target = @"D:\Restore", IReadOnlyList<string>? selection = null, string mode = RestoreModes.Rename) => new()
    {
        RestoreJobId = "r1", RecoveryPointId = "rp1", RepositoryId = "repo", Engine = BackupEngineNames.Kopia, EngineSnapshotId = "k1a2b3",
        SourceRoot = @"D:\Tally\Data", TargetKind = RestoreTargetKinds.Alternate, TargetPath = target, Mode = mode, Selection = selection ?? []
    };

    [Fact]
    public void ValidRestore_Passes() => Assert.Null(RestoreExecuteHandler.Validate(Restore(selection: ["Company\\001", "a/b.txt"])));

    [Theory]
    [InlineData("..\\Windows")]
    [InlineData("C:\\Windows\\system32")]
    [InlineData("a/../../b")]
    [InlineData("file.txt:stream")]
    public void TraversalOrAbsoluteSelections_AreRejected(string item) => Assert.NotNull(RestoreExecuteHandler.Validate(Restore(selection: [item])));

    [Fact]
    public void UncTargetAndUnknownMode_AreRejected()
    {
        Assert.NotNull(RestoreExecuteHandler.Validate(Restore(target: @"\\server\share")));
        Assert.NotNull(RestoreExecuteHandler.Validate(Restore(mode: "merge")));
    }

    [Fact]
    public void Update_RequiresHttpsAndPinnedHash()
    {
        var ok = new AgentUpdateParameters { UpdateId = "u1", Version = "1.4.0", PackageUrl = "https://downloads.example.test/a.exe", Sha256 = new string('a', 64) };
        Assert.Null(AgentUpdateHandler.Validate(ok));
        Assert.NotNull(AgentUpdateHandler.Validate(new AgentUpdateParameters { UpdateId = "u1", Version = "1.4.0", PackageUrl = "http://downloads.example.test/a.exe", Sha256 = new string('a', 64) }));
        Assert.NotNull(AgentUpdateHandler.Validate(new AgentUpdateParameters { UpdateId = "u1", Version = "1.4.0", PackageUrl = "https://downloads.example.test/a.exe", Sha256 = "abc" }));
        Assert.NotNull(AgentUpdateHandler.Validate(new AgentUpdateParameters { UpdateId = "u1", Version = "latest", PackageUrl = "https://downloads.example.test/a.exe", Sha256 = new string('a', 64) }));
    }
}
