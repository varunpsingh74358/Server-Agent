using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.Agent.Contracts.Identity;
using CloudOrc.ControlAgent.Backend;
using CloudOrc.ControlAgent.Backup;
using CloudOrc.ControlAgent.Commands;
using CloudOrc.ControlAgent.Configuration;
using CloudOrc.ControlAgent.DurableQueue;
using CloudOrc.ControlAgent.Enrollment;
using CloudOrc.ControlAgent.ResourceGovernor;
using CloudOrc.ControlAgent.Transport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudOrc.ControlAgent.Tests.Backup;

/// <summary>
/// Whole agent pipeline with real components and a fake HTTPS backend on loopback:
/// typed command → durable job → admission → grant → REAL worker process inside a REAL Job
/// Object → result → durable events → outbox → HTTP delivery. No engines are installed, so the
/// worker must fail closed (ENGINE_BINARY_MISSING) and the backend must be told so, in order.
/// </summary>
public sealed class AgentPipelineEndToEndTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cloudorc-e2e-" + Guid.NewGuid().ToString("N"));
    private readonly HttpListener _backend = new();
    private readonly ConcurrentQueue<(string Path, string Body)> _calls = new();
    private readonly int _port;

    public AgentPipelineEndToEndTests()
    {
        Directory.CreateDirectory(_dir);
        _port = FreePort();
        _backend.Prefixes.Add($"http://localhost:{_port}/");
        _backend.Start();
        _ = Task.Run(ServeAsync);
    }

    public void Dispose()
    {
        _backend.Stop();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string? WorkerExe()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CloudOrc.WindowsAgents.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        if (dir is null)
        {
            return null;
        }

        return new[] { "Debug", "Release" }
            .Select(c => Path.Combine(dir, "src", "CloudOrc.BackupWorker", "bin", c, "net10.0-windows", "CloudOrc.BackupWorker.exe"))
            .FirstOrDefault(File.Exists);
    }

    [Fact]
    public async Task PolicyApply_BackupRun_RealWorker_FailsClosed_AndEventsReachTheBackendInOrder()
    {
        var workerExe = WorkerExe();
        Assert.True(workerExe is not null, "Build src/CloudOrc.BackupWorker first (dotnet build the solution).");

        // ---- composition (mirrors Program.cs)
        var controlOptions = Options.Create(new ControlAgentOptions { DataDirectory = _dir });
        var backupOptions = Options.Create(new BackupOptions
        {
            WorkerExecutablePath = workerExe,
            EngineDirectory = Path.Combine(_dir, "no-engines"),
            RetryBaseSeconds = 3600
        });
        var connection = Options.Create(new BackendConnectionOptions { Url = $"ws://localhost:{_port}/agent", DevelopmentAllowInsecureWs = true });
        var identity = new AgentIdentity { AgentId = "agent-e2e", ServerId = "srv", MachineId = "m", MachineName = "n", AgentVersion = "1.0.0", Credential = "cred-e2e" };
        var store = new EnrolledStateStore(controlOptions, NullLogger<EnrolledStateStore>.Instance);
        var paths = new BackupPaths(controlOptions, backupOptions);
        using var db = new AgentStateDatabase(paths.StateDatabase, NullLogger<AgentStateDatabase>.Instance, hardenDirectoryAcl: false);
        var inbox = new CommandInboxStore(db);
        var outbox = new OutboxStore(db);
        var policies = new BackupPolicyStore(db);
        var jobs = new BackupJobStore(db);
        using var api = new BackendApiClient(backupOptions, connection, store, new AgentCredentialProvider(identity, store), TimeProvider.System, NullLogger<BackendApiClient>.Instance);
        var events = new JobEventRecorder(jobs, identity.AgentId, TimeProvider.System, TimeSpan.FromSeconds(30));
        var governor = new WindowsResourceGovernor(backupOptions, NullLogger<WindowsResourceGovernor>.Instance);
        var runner = new BackupJobRunner(jobs, policies, events, new BackupControlPlane(api), governor,
            new ProcessWorkerHost(paths, governor, NullLogger<ProcessWorkerHost>.Instance), paths, outbox, identity, backupOptions,
            TimeProvider.System, NullLogger<BackupJobRunner>.Instance);
        var coordinator = new BackupJobCoordinator(jobs, inbox, runner, TimeProvider.System, NullLogger<BackupJobCoordinator>.Instance);
        var scheduler = new BackupSchedulerService(policies, jobs, runner, coordinator, identity, backupOptions, TimeProvider.System, NullLogger<BackupSchedulerService>.Instance);
        var outgoing = new OutgoingMessageChannel();
        var dispatcher = new TypedCommandDispatcher(
            [new PolicyApplyHandler(policies, scheduler, identity, TimeProvider.System), new BackupRunHandler(policies, jobs, coordinator, TimeProvider.System)],
            inbox, outgoing, TimeProvider.System, NullLogger<TypedCommandDispatcher>.Instance);
        var delivery = new OutboxDispatcherService(outbox, api, new BackendLinkState(), TimeProvider.System, NullLogger<OutboxDispatcherService>.Instance);

        // ---- 1. backend applies a policy and asks for an on-demand backup (typed commands)
        var policy = BackupTestData.Policy(sources: [new BackupSource { Path = _dir }], schedule: new BackupSchedule { Kind = BackupScheduleKinds.Manual });
        await dispatcher.DispatchAsync(Command("cmd-policy", BackupCommandTypes.PolicyApply, new PolicyApplyParameters { Policy = policy }), BackupCommandTypes.PolicyApply);
        await dispatcher.DispatchAsync(Command("cmd-run", BackupCommandTypes.Run, new BackupRunParameters { JobId = "job-e2e", PolicyId = policy.PolicyId }), BackupCommandTypes.Run);
        Assert.NotNull(policies.Get(policy.PolicyId));
        Assert.Equal(BackupJobStates.Queued, jobs.Get("job-e2e")!.State);

        // ---- 2. coordinator runs it end to end with the real worker process
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await coordinator.StartAsync(cts.Token);
        while (!BackupJobStates.IsTerminal(jobs.Get("job-e2e")!.State) && !cts.IsCancellationRequested)
        {
            await Task.Delay(200, cts.Token);
        }

        await coordinator.StopAsync(CancellationToken.None);

        var job = jobs.Get("job-e2e")!;
        Assert.Equal(BackupJobStates.Failed, job.State);
        Assert.Equal(BackupReasonCodes.EngineBinaryMissing, job.ReasonCode);

        // ---- 3. durable events are delivered to the backend, in sequence
        await delivery.DrainAsync(CancellationToken.None);
        Assert.Empty(outbox.PeekHead(10));

        var paths2 = _calls.Select(c => c.Path).ToList();
        Assert.Contains("/api/v1/backup/admission/request", paths2);
        Assert.Contains("/api/v1/storage/grants", paths2);
        Assert.Contains("/api/v1/backup/admission/lease-e2e/release", paths2);

        var delivered = _calls.Where(c => c.Path == "/api/v1/backup/jobs/job-e2e/events")
            .Select(c => JsonSerializer.Deserialize<BackupJobEvent>(c.Body, BackendApiClient.SnakeCaseJson)!).ToList();
        Assert.Equal(BackupEventTypes.Started, delivered.First().EventType);
        Assert.Equal(BackupEventTypes.Failed, delivered.Last().EventType);
        Assert.Equal(delivered.Select(e => e.Sequence).OrderBy(s => s), delivered.Select(e => e.Sequence));
        Assert.All(delivered, e => Assert.Equal(7, e.FencingToken ?? 7));
        Assert.All(_calls, c => Assert.DoesNotContain("e2e-repo-password", c.Body)); // grant secret never echoed back

        // ---- 4. the backend learned the command results (idempotent ACK/result)
        var results = Drain(outgoing).Where(m => m.Contains("COMMAND_RESULT", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, results.Count);
    }

    private static string Command(string id, string type, object parameters) =>
        JsonSerializer.Serialize(new { type = "COMMAND", commandId = id, correlationId = "corr-" + id, commandType = type, commandVersion = "1.0", parameters },
            Agent.Contracts.Protocol.ProtocolJson.Options);

    private async Task ServeAsync()
    {
        while (_backend.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _backend.GetContextAsync();
            }
            catch (Exception)
            {
                return;
            }

            var path = ctx.Request.Url!.AbsolutePath;
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            _calls.Enqueue((path, body));

            string response = "{}";
            if (path == "/api/v1/backup/admission/request")
            {
                response = JsonSerializer.Serialize(new AdmissionResponse
                {
                    Decision = AdmissionDecisions.Granted,
                    Lease = new AdmissionLease { LeaseId = "lease-e2e", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10), FencingToken = 7, MaxParallel = 1, MaxUploadMbps = 10 }
                }, BackendApiClient.SnakeCaseJson);
            }
            else if (path == "/api/v1/storage/grants")
            {
                response = JsonSerializer.Serialize(new StorageGrant
                {
                    GrantId = "grant-e2e",
                    Provider = StorageProviderTypes.S3,
                    ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                    Repository = new GrantRepository { RepositoryId = "repo-1", Engine = BackupEngineNames.Kopia, Path = "repo-1", Password = "e2e-repo-password" },
                    S3 = new S3Grant { Endpoint = "https://s3.example.test", Bucket = "b", AccessKeyId = "AKIAEXAMPLE000000000", SecretAccessKey = "secret" }
                }, BackendApiClient.SnakeCaseJson);
            }

            var bytes = Encoding.UTF8.GetBytes(response);
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    private static List<string> Drain(OutgoingMessageChannel channel)
    {
        var list = new List<string>();
        using var cts = new CancellationTokenSource(200);
        try
        {
            var e = channel.ReadAllAsync(cts.Token).GetAsyncEnumerator(cts.Token);
            while (e.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                list.Add(e.Current);
            }
        }
        catch (OperationCanceledException)
        {
        }

        return list;
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
