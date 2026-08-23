using CloudOrc.Agent.Contracts.Abstractions;
using CloudOrc.ControlAgent.Backend;
using CloudOrc.ControlAgent.Configuration;
using CloudOrc.ControlAgent.Health;
using CloudOrc.ControlAgent.Identity;
using CloudOrc.ControlAgent.Services;
using CloudOrc.ControlAgent.Startup;
using CloudOrc.ControlAgent.Telemetry;
using Microsoft.Extensions.Hosting.WindowsServices;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<ControlAgentOptions>(builder.Configuration.GetSection(ControlAgentOptions.SectionName));
builder.Services.Configure<BackendConnectionOptions>(builder.Configuration.GetSection(BackendConnectionOptions.SectionName));
builder.Services.Configure<AgentIdentityOptions>(builder.Configuration.GetSection(AgentIdentityOptions.SectionName));

var controlAgentOptions =
    builder.Configuration.GetSection(ControlAgentOptions.SectionName).Get<ControlAgentOptions>()
    ?? new ControlAgentOptions();

var backendOptions =
    builder.Configuration.GetSection(BackendConnectionOptions.SectionName).Get<BackendConnectionOptions>()
    ?? new BackendConnectionOptions();

// Directories must exist before Serilog's file sink (below) tries to write into logs\.
DirectoryBootstrapper.EnsureDirectories(controlAgentOptions);

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
// connection unless the operator has explicitly opted into development mode.
if (backendOptions.Enabled)
{
    var isInsecureWs = backendOptions.Url.StartsWith("ws://", StringComparison.OrdinalIgnoreCase);
    if (isInsecureWs && !backendOptions.DevelopmentAllowInsecureWs)
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
        "CloudOrc Control Agent starting. Data directory: {DataDirectory}. LocalFileMode={LocalFileMode}, BackendConnection={BackendEnabled}.",
        controlAgentOptions.DataDirectory, controlAgentOptions.LocalFileModeEnabled, backendOptions.Enabled);
    host.Run();
}
finally
{
    Log.CloseAndFlush();
}
