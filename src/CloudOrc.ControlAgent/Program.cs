using CloudOrc.Agent.Contracts.Abstractions;
using CloudOrc.Agent.Contracts.Versioning;
using CloudOrc.ControlAgent.Backend;
using CloudOrc.ControlAgent.Configuration;
using CloudOrc.ControlAgent.Enrollment;
using CloudOrc.ControlAgent.Health;
using CloudOrc.ControlAgent.Identity;
using CloudOrc.ControlAgent.Services;
using CloudOrc.ControlAgent.Startup;
using CloudOrc.ControlAgent.Telemetry;
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

    var enrollControlAgentOptions =
        enrollConfiguration.GetSection(ControlAgentOptions.SectionName).Get<ControlAgentOptions>()
        ?? new ControlAgentOptions();

    using var enrollLoggerFactory = LoggerFactory.Create(b => b.AddConsole());
    return await EnrollmentCommandLine.RunAsync(args, enrollControlAgentOptions, enrollLoggerFactory).ConfigureAwait(false);
}

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<ControlAgentOptions>(builder.Configuration.GetSection(ControlAgentOptions.SectionName));
builder.Services.Configure<AgentIdentityOptions>(builder.Configuration.GetSection(AgentIdentityOptions.SectionName));

var controlAgentOptions =
    builder.Configuration.GetSection(ControlAgentOptions.SectionName).Get<ControlAgentOptions>()
    ?? new ControlAgentOptions();

var backendOptions =
    builder.Configuration.GetSection(BackendConnectionOptions.SectionName).Get<BackendConnectionOptions>()
    ?? new BackendConnectionOptions();

// Directories must exist before Serilog's file sink (below) tries to write into logs\.
DirectoryBootstrapper.EnsureDirectories(controlAgentOptions);

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
builder.Services.AddSingleton<ICommandQueue, InMemoryCommandQueue>();
builder.Services.AddSingleton<IPowerShellExecutor, PowerShellCommandExecutor>();

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

    builder.Services.AddHostedService<BackendConnectionService>();
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
