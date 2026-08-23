using System.IO.Pipes;
using System.Text.Json;
using CloudOrc.Agent.Contracts.Health;
using CloudOrc.WatchdogAgent.Configuration;
using Microsoft.Extensions.Logging;

namespace CloudOrc.WatchdogAgent.ControlAgentManagement;

/// <summary>
/// Named Pipe client that asks the Control Agent for its current
/// <see cref="ControlAgentHealthSnapshot"/>. A connection failure or timeout is treated
/// as "unhealthy" rather than thrown - from the Watchdog's point of view, an agent that
/// cannot be reached at all is exactly as concerning as one that responds unhealthy.
/// </summary>
public sealed class ControlAgentHealthClient(WatchdogOptions options, ILogger<ControlAgentHealthClient> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ControlAgentHealthSnapshot?> TryGetHealthAsync(CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(options.HealthCheckTimeoutSeconds));

        try
        {
            await using var client = new NamedPipeClientStream(".", options.HealthPipeName, PipeDirection.In, PipeOptions.Asynchronous);
            await client.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);

            using var buffer = new MemoryStream();
            await client.CopyToAsync(buffer, timeoutCts.Token).ConfigureAwait(false);
            buffer.Position = 0;

            return await JsonSerializer.DeserializeAsync<ControlAgentHealthSnapshot>(buffer, JsonOptions, timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Timed out connecting to the Control Agent health pipe '{PipeName}' within {TimeoutSeconds}s. Treating as unhealthy.",
                options.HealthPipeName, options.HealthCheckTimeoutSeconds);
            return null;
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Could not reach the Control Agent health pipe '{PipeName}'. Treating as unhealthy.", options.HealthPipeName);
            return null;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Control Agent health pipe returned malformed data. Treating as unhealthy.");
            return null;
        }
    }
}
