using CloudOrc.Agent.Contracts.Abstractions;
using CloudOrc.Agent.Contracts.Identity;
using CloudOrc.Agent.Contracts.Versioning;
using CloudOrc.ControlAgent.Backend;
using CloudOrc.ControlAgent.Backup;
using CloudOrc.ControlAgent.Commands;
using CloudOrc.ControlAgent.Configuration;
using CloudOrc.ControlAgent.DurableQueue;
using CloudOrc.ControlAgent.Enrollment;
using CloudOrc.ControlAgent.Health;
using CloudOrc.ControlAgent.Identity;
using CloudOrc.ControlAgent.LocalStatus;
using CloudOrc.ControlAgent.RdpTransferPolicy;
using CloudOrc.ControlAgent.ResourceGovernor;
using CloudOrc.ControlAgent.Services;
using CloudOrc.ControlAgent.Startup;
using CloudOrc.ControlAgent.Telemetry;
using CloudOrc.ControlAgent.Transport;
using CloudOrc.ControlAgent.Updates;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Serilog;

// `--version`/`-v` is checked first, before anything else (including `enroll`) is parsed -
// it must work even with no appsettings.json present and never touches any state. This is
// what the installer's own --version handling, and any operator/script checking "what did
// the upgrade actually install", relies on. See docs/INSTALLATION.md.
if (args.Length > 0 && (string.Equals(args[0], "--version", StringComparison.OrdinalIgnoreCase) || string.Equals(args[0], "-v", StringComparison.OrdinalIgnoreCase)))
{
    Console.WriteLine("CloudOrc ControlAgent");
    Console.WriteLine($"Version: {AgentVersionInfo.GetVersion()}");
    return 0;
}

// `enroll` is a one-shot CLI mode (used by the installer, or manually for re-enrollment)
// that runs BEFORE the normal Worker Service host is built - it never starts the host,
// never opens the backend connection itself, and exits immediately with a code the caller
// (the installer) checks. See docs/ENROLLMENT.md.
if (args.Length > 0 && string.Equals(args[0], "enroll", StringComparison.OrdinalIgnoreCase))
{
    var enrollConfiguration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true)
        .Build();
    enrollConfiguration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
        .AddConfiguration(enrollConfiguration)
        .AddJsonFile(ActivationSettingsFile.PathFor(
            (enrollConfiguration.GetSection(ControlAgentOptions.SectionName).Get<ControlAgentOptions>() ?? new ControlAgentOptions()).DataDirectory), optional: true)
        .Build();

    var enrollControlAgentOptions =
        enrollConfiguration.GetSection(ControlAgentOptions.SectionName).Get<ControlAgentOptions>()
        ?? new ControlAgentOptions();
    var enrollActivationOptions =
        enrollConfiguration.GetSection(ActivationOptions.SectionName).Get<ActivationOptions>()
        ?? new ActivationOptions();

    using var enrollLoggerFactory = LoggerFactory.Create(b => b.AddConsole());
    return await EnrollmentCommandLine.RunAsync(args, enrollControlAgentOptions, enrollActivationOptions, enrollLoggerFactory).ConfigureAwait(false);
}

// `set-activation --api-base https://... [--portal-url https://...]` persists where an
// un-activated device redeems activation codes (CO-DEV-BKP-001 section 4). Written by the
// installer from the portal download; not a secret; https only.
if (args.Length > 0 && string.Equals(args[0], "set-activation", StringComparison.OrdinalIgnoreCase))
{
    var setConfiguration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true)
        .Build();
    var setOptions = setConfiguration.GetSection(ControlAgentOptions.SectionName).Get<ControlAgentOptions>() ?? new ControlAgentOptions();
    return ActivationSettingsFile.RunCommandLine(args, setOptions.DataDirectory);
}

// `restart-service` is a one-shot helper the running service launches (detached) after a local
// activation from the tray, so it comes back up in enrolled mode. It only ever restarts this
// agent's own service - no arbitrary commands.
if (args.Length > 0 && string.Equals(args[0], "restart-service", StringComparison.OrdinalIgnoreCase))
{
    await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
    using var controller = new System.ServiceProcess.ServiceController("CloudOrcControlAgent");
    try
    {
        if (controller.Status is not System.ServiceProcess.ServiceControllerStatus.Stopped)
        {
            controller.Stop();
            controller.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
        }

        controller.Start();
        return 0;
    }
    catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException or System.ComponentModel.Win32Exception)
    {
        Console.Error.WriteLine($"Could not restart CloudOrcControlAgent: {ex.Message}");
        return 1;
    }
}

var builder = Host.CreateApplicationBuilder(args);
{
    var bootstrapOptions = builder.Configuration.GetSection(ControlAgentOptions.SectionName).Get<ControlAgentOptions>() ?? new ControlAgentOptions();
    builder.Configuration.AddJsonFile(ActivationSettingsFile.PathFor(bootstrapOptions.DataDirectory), optional: true, reloadOnChange: false);
}

builder.Services.Configure<ControlAgentOptions>(builder.Configuration.GetSection(ControlAgentOptions.SectionName));
builder.Services.Configure<AgentIdentityOptions>(builder.Configuration.GetSection(AgentIdentityOptions.SectionName));
builder.Services.Configure<BackupOptions>(builder.Configuration.GetSection(BackupOptions.SectionName));
builder.Services.Configure<ActivationOptions>(builder.Configuration.GetSection(ActivationOptions.SectionName));
builder.Services.Configure<UpdateOptions>(builder.Configuration.GetSection(UpdateOptions.SectionName));

var backupOptions =
    builder.Configuration.GetSection(BackupOptions.SectionName).Get<BackupOptions>()
    ?? new BackupOptions();

var controlAgentOptions =
    builder.Configuration.GetSection(ControlAgentOptions.SectionName).Get<ControlAgentOptions>()
    ?? new ControlAgentOptions();

var backendOptions =
    builder.Configuration.GetSection(BackendConnectionOptions.SectionName).Get<BackendConnectionOptions>()
    ?? new BackendConnectionOptions();

// Directories must exist before Serilog's file sink (below) tries to write into logs\.
DirectoryBootstrapper.EnsureDirectories(controlAgentOptions);
if (OperatingSystem.IsWindows() && controlAgentOptions.HardenDataDirectoryAcl
    && DataDirectoryAcl.Harden(controlAgentOptions.DataDirectory) is { } aclError)
{
    Console.Error.WriteLine($"WARNING: could not restrict {controlAgentOptions.DataDirectory} to SYSTEM and Administrators: {aclError}");
}

// If this agent has been enrolled, the persisted, backend-issued BackendUrl always wins
// over whatever is (or isn't) in appsettings.json - enrollment, not manual configuration,
// is the source of truth once it has happened. A throwaway logger is fine here: this is a
// one-time startup check before Serilog exists yet, and any read failure already logs
// through EnrolledStateStore's own error handling and is treated as "not enrolled".
var enrolledState = new EnrolledStateStore(Options.Create(controlAgentOptions), NullLogger<EnrolledStateStore>.Instance).TryLoad();
var backendUrlIsFromEnrollment = enrolledState is not null;
if (enrolledState is not null)
{
    backendOptions.Enabled = true;
    backendOptions.Url = enrolledState.BackendUrl;
}

builder.Logging.ClearProviders();
builder.Services.AddSerilog((services, loggerConfiguration) => loggerConfiguration
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(controlAgentOptions.LogsDirectory, "controlagent-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"));

// Fail fast on an unsafe backend configuration - never silently allow an insecure ws://
// connection unless the operator has explicitly opted into development mode. This check
// does not apply to a URL that came from enrollment: that value was issued by an
// authenticated backend response, not typed by a human into a config file, so the
// human-error guard rail it exists for does not apply.
if (backendOptions.Enabled)
{
    var isInsecureWs = backendOptions.Url.StartsWith("ws://", StringComparison.OrdinalIgnoreCase);
    if (isInsecureWs && !backendOptions.DevelopmentAllowInsecureWs && !backendUrlIsFromEnrollment)
    {
        throw new InvalidOperationException(
            $"BackendConnection.Url ('{backendOptions.Url}') uses insecure ws://, but " +
            $"BackendConnection.DevelopmentAllowInsecureWs is false. Either use a wss:// URL, " +
            $"or set DevelopmentAllowInsecureWs to true for LOCAL TESTING ONLY. Refusing to start.");
    }

    if (string.IsNullOrWhiteSpace(backendOptions.Url))
    {
        throw new InvalidOperationException("BackendConnection.Enabled is true but BackendConnection.Url is empty.");
    }
}

if (!controlAgentOptions.LocalFileModeEnabled && !backendOptions.Enabled)
{
    Log.Warning("Both ControlAgent.LocalFileModeEnabled and BackendConnection.Enabled are false - this agent will not receive any commands from any source.");
}

// Runs as a normal console app under `dotnet run`; automatically switches to Windows
// Service lifetime when launched by the Service Control Manager. See
// docs/WINDOWS_SERVICE_INSTALLATION.md for how to install it as CloudOrcControlAgent.
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "CloudOrcControlAgent";
});

// Registered as the already-computed instance (rather than bound fresh from config again)
// so every consumer sees the same, possibly enrollment-overridden, Url/Enabled values.
builder.Services.AddSingleton<IOptions<BackendConnectionOptions>>(Options.Create(backendOptions));

builder.Services.AddSingleton<EnrolledStateStore>();
builder.Services.AddSingleton<ControlAgentHealthState>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<BackupPaths>();
builder.Services.AddSingleton<BackendLinkState>();

// ---- Durable local state (CO-DEV-BKP-001 §3/§8/§11/§13): always available, even before activation.
builder.Services.AddSingleton(sp => new AgentStateDatabase(
    sp.GetRequiredService<BackupPaths>().StateDatabase,
    sp.GetRequiredService<ILogger<AgentStateDatabase>>()));
builder.Services.AddSingleton(sp => new CommandInboxStore(sp.GetRequiredService<AgentStateDatabase>()));
builder.Services.AddSingleton(sp => new OutboxStore(sp.GetRequiredService<AgentStateDatabase>()));
builder.Services.AddSingleton(sp => new BackupPolicyStore(sp.GetRequiredService<AgentStateDatabase>()));
builder.Services.AddSingleton(sp => new BackupJobStore(sp.GetRequiredService<AgentStateDatabase>()));
builder.Services.AddHostedService<DurableStateRetentionService>();

// ---- Agent Core's own (parent) budget, §7: measure, trim when idle, flag breaches.
builder.Services.AddSingleton<CoreBudgetMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CoreBudgetMonitor>());

// ---- Local status IPC for the optional tray (§4): works enrolled or not.
builder.Services.AddSingleton<ActivationService>();
builder.Services.AddHostedService<AgentStatusPipeServer>();
builder.Services.AddSingleton<ICommandQueue, InMemoryCommandQueue>();
builder.Services.AddSingleton<IPowerShellExecutor, PowerShellCommandExecutor>();
builder.Services.AddSingleton<IRdpOsBuildDetector, WindowsRdpOsBuildDetector>();
builder.Services.AddSingleton<IRdpPolicyRegistryAccessor, WindowsRdpPolicyRegistryAccessor>();
builder.Services.AddSingleton<IRdpTransferPolicyExecutor, WindowsRdpTransferPolicyManager>();

if (controlAgentOptions.LocalFileModeEnabled)
{
    builder.Services.AddSingleton<ICommandSource, LocalFileCommandSource>();
    builder.Services.AddSingleton<ICommandResultSink, LocalFileResultSink>();
}

if (backendOptions.Enabled)
{
    builder.Services.AddSingleton<AgentIdentityProvider>();
    builder.Services.AddSingleton(sp => sp.GetRequiredService<AgentIdentityProvider>().GetIdentity());
    builder.Services.AddSingleton<OutgoingMessageChannel>();
    builder.Services.AddSingleton<WssCommandSource>();
    builder.Services.AddSingleton<ICommandSource>(sp => sp.GetRequiredService<WssCommandSource>());
    builder.Services.AddSingleton<ICommandResultSink, WssResultSink>();
    builder.Services.AddSingleton<ICommandStatusPublisher, BackendCommandStatusPublisher>();
    builder.Services.AddSingleton<TelemetryCollector>();

    // ---- Transport: renewable credential, HTTPS control plane, device key, capability profile.
    builder.Services.AddSingleton<AgentCredentialProvider>();
    builder.Services.AddSingleton<BackendApiClient>();
    builder.Services.AddSingleton(_ => new DeviceKeyStore());
    builder.Services.AddSingleton<SessionRenewalService>();
    builder.Services.AddSingleton<ISessionRenewer>(sp => sp.GetRequiredService<SessionRenewalService>());
    builder.Services.AddSingleton<IAgentCapabilitiesProvider, AgentCapabilitiesProvider>();
    builder.Services.AddHostedService<CredentialRenewalScheduler>();
    builder.Services.AddSingleton<CertificateRotationService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<CertificateRotationService>());
    builder.Services.AddSingleton<ITypedCommandHandler, CertificateRenewHandler>();

    // ---- Durable outbox delivery (§11 at-least-once) + updater result reporting (§13).
    builder.Services.AddSingleton<OutboxDispatcherService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<OutboxDispatcherService>());
    builder.Services.AddHostedService<UpdateResultReporter>();

    // ---- Typed commands (§10): allow-listed, versioned, idempotent via the durable inbox.
    builder.Services.AddSingleton(sp => new CommandSignatureVerifier(
        sp.GetRequiredService<EnrolledStateStore>().TryLoad()?.CommandSigningPublicKey,
        controlAgentOptions.RequireSignedTypedCommands,
        sp.GetRequiredService<TimeProvider>()));
    builder.Services.AddSingleton(sp => new TypedCommandDispatcher(
        sp.GetServices<ITypedCommandHandler>(),
        sp.GetRequiredService<CommandInboxStore>(),
        sp.GetRequiredService<OutgoingMessageChannel>(),
        sp.GetRequiredService<TimeProvider>(),
        sp.GetRequiredService<ILogger<TypedCommandDispatcher>>(),
        sp.GetRequiredService<CommandSignatureVerifier>()));
    builder.Services.AddSingleton<ITypedCommandRouter>(sp => sp.GetRequiredService<TypedCommandDispatcher>());
    builder.Services.AddSingleton<ITypedCommandHandler, AgentUpdateHandler>();
    builder.Services.AddSingleton<ITypedCommandHandler, CredentialRenewHandler>();

    if (backupOptions.Enabled)
    {
        // ---- Backup capability host (§3 Agent Core / Worker, §7 governor, §8 admission/leases).
        builder.Services.AddSingleton<IBackupControlPlane, BackupControlPlane>();
        builder.Services.AddSingleton<IResourceGovernor, WindowsResourceGovernor>();
        builder.Services.AddSingleton<IWorkerHost, ProcessWorkerHost>();
        builder.Services.AddSingleton(sp =>
        {
            var recorder = new JobEventRecorder(
                sp.GetRequiredService<BackupJobStore>(),
                sp.GetRequiredService<AgentIdentity>().AgentId,
                sp.GetRequiredService<TimeProvider>(),
                TimeSpan.FromSeconds(Math.Max(5, backupOptions.ProgressEventIntervalSeconds)));
            var dispatcher = sp.GetRequiredService<OutboxDispatcherService>();
            recorder.EventQueued += dispatcher.Wake;
            return recorder;
        });
        builder.Services.AddSingleton<BackupJobRunner>();
        builder.Services.AddSingleton<BackupJobCoordinator>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<BackupJobCoordinator>());
        builder.Services.AddSingleton<BackupSchedulerService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<BackupSchedulerService>());

        builder.Services.AddSingleton<ITypedCommandHandler, PolicyApplyHandler>();
        builder.Services.AddSingleton<ITypedCommandHandler, PolicyRemoveHandler>();
        builder.Services.AddSingleton<ITypedCommandHandler, BackupRunHandler>();
        builder.Services.AddSingleton<ITypedCommandHandler, JobCancelHandler>();
        builder.Services.AddSingleton<ITypedCommandHandler, VerifyHandler>();
        builder.Services.AddSingleton<ITypedCommandHandler, MaintenanceHandler>();
        builder.Services.AddSingleton<ITypedCommandHandler, RestoreExecuteHandler>();
    }

    builder.Services.AddHostedService(sp => new BackendConnectionService(
        sp.GetRequiredService<IOptions<BackendConnectionOptions>>(),
        sp.GetRequiredService<AgentIdentity>(),
        sp.GetRequiredService<OutgoingMessageChannel>(),
        sp.GetRequiredService<WssCommandSource>(),
        sp.GetRequiredService<ControlAgentHealthState>(),
        sp.GetRequiredService<ILogger<BackendConnectionService>>(),
        sp.GetRequiredService<AgentCredentialProvider>(),
        sp.GetRequiredService<ITypedCommandRouter>(),
        sp.GetRequiredService<IAgentCapabilitiesProvider>(),
        sp.GetRequiredService<ISessionRenewer>(),
        sp.GetRequiredService<BackendLinkState>(),
        sp.GetRequiredService<IOptions<ControlAgentOptions>>(),
        sp.GetRequiredService<EnrolledStateStore>()));
    builder.Services.AddHostedService<HeartbeatPublisherService>();
    builder.Services.AddHostedService<TelemetryPublisherService>();
}
else
{
    builder.Services.AddSingleton<ICommandStatusPublisher, NullCommandStatusPublisher>();
}

builder.Services.AddHostedService<CommandDetectionService>();
builder.Services.AddHostedService<CommandProcessingService>();
builder.Services.AddHostedService<HealthPipeServer>();

var host = builder.Build();

try
{
    Log.Information(
        "CloudOrc Control Agent starting. Data directory: {DataDirectory}. LocalFileMode={LocalFileMode}, BackendConnection={BackendEnabled}, Enrolled={Enrolled}.",
        controlAgentOptions.DataDirectory, controlAgentOptions.LocalFileModeEnabled, backendOptions.Enabled, backendUrlIsFromEnrollment);
    host.Run();
}
finally
{
    Log.CloseAndFlush();
}

return 0;
