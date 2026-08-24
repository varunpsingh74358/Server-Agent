using CloudOrc.Agent.Contracts.Versioning;
using CloudOrc.WatchdogAgent.Configuration;
using CloudOrc.WatchdogAgent.ControlAgentManagement;
using CloudOrc.WatchdogAgent.Recovery;
using CloudOrc.WatchdogAgent.Services;
using Serilog;

// Same "--version"/"-v" convention as CloudOrc.ControlAgent.exe (see its Program.cs) - kept
// consistent so any script/operator that checks the Control Agent's version can check the
// Watchdog's the same way. Not itself required by the installer, which only shells out to
// the Control Agent's CLI.
if (args.Length > 0 && (string.Equals(args[0], "--version", StringComparison.OrdinalIgnoreCase) || string.Equals(args[0], "-v", StringComparison.OrdinalIgnoreCase)))
{
    Console.WriteLine("CloudOrc WatchdogAgent");
    Console.WriteLine($"Version: {AgentVersionInfo.GetVersion()}");
    return 0;
}

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<WatchdogOptions>(builder.Configuration.GetSection(WatchdogOptions.SectionName));

var watchdogOptions =
    builder.Configuration.GetSection(WatchdogOptions.SectionName).Get<WatchdogOptions>()
    ?? new WatchdogOptions();

Directory.CreateDirectory(watchdogOptions.DataDirectory);
Directory.CreateDirectory(watchdogOptions.LogsDirectory);

builder.Logging.ClearProviders();
builder.Services.AddSerilog((services, loggerConfiguration) => loggerConfiguration
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(watchdogOptions.LogsDirectory, "watchdogagent-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"));

// Runs as a normal console app under `dotnet run`; automatically switches to Windows
// Service lifetime when launched by the Service Control Manager. See
// docs/WINDOWS_SERVICE_INSTALLATION.md for how to install it as CloudOrcWatchdogAgent.
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "CloudOrcWatchdogAgent";
});

builder.Services.AddSingleton(watchdogOptions);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ControlAgentServiceManager>();
builder.Services.AddSingleton<ControlAgentHealthClient>();
builder.Services.AddSingleton<ConsecutiveFailureTracker>();
builder.Services.AddSingleton<RecoveryRateLimiter>();

builder.Services.AddHostedService<WatchdogMonitorService>();

var host = builder.Build();

try
{
    Log.Information(
        "CloudOrc Watchdog Agent starting. Monitoring service '{ServiceName}'.",
        watchdogOptions.ControlAgentServiceName);
    host.Run();
}
finally
{
    Log.CloseAndFlush();
}

return 0;
