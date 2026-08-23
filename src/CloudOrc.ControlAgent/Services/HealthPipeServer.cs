using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using CloudOrc.ControlAgent.Configuration;
using CloudOrc.ControlAgent.Health;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudOrc.ControlAgent.Services;

/// <summary>
/// Serves the current <see cref="ControlAgentHealthState"/> snapshot as JSON over a local
/// Named Pipe, so the Watchdog Agent can ask "are you actually healthy?" without any
/// network exposure. One client is served at a time, which is more than sufficient for a
/// single local Watchdog polling every few seconds.
/// </summary>
public sealed class HealthPipeServer(
    ControlAgentHealthState health,
    IOptions<ControlAgentOptions> options,
    ILogger<HealthPipeServer> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ControlAgentOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Health pipe server listening on pipe '{PipeName}'.", _options.HealthPipeName);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _options.HealthPipeName,
                    PipeDirection.Out,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);

                var snapshot = health.Snapshot(TimeSpan.FromSeconds(_options.WorkerHeartbeatTimeoutSeconds));
                var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(snapshot, JsonOptions));

                await server.WriteAsync(payload, stoppingToken).ConfigureAwait(false);
                await server.FlushAsync(stoppingToken).ConfigureAwait(false);
                server.WaitForPipeDrain();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Health pipe connection ended unexpectedly; will accept the next connection.");
            }
        }

        logger.LogInformation("Health pipe server stopped.");
    }
}
