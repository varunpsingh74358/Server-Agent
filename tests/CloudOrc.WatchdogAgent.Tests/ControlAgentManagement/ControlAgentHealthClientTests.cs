using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using CloudOrc.Agent.Contracts.Health;
using CloudOrc.WatchdogAgent.ControlAgentManagement;
using CloudOrc.WatchdogAgent.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudOrc.WatchdogAgent.Tests.ControlAgentManagement;

/// <summary>
/// Verifies the Watchdog's health pipe client against real named pipes (no Windows
/// Service involved) - both the "nobody is listening" unhealthy path and successfully
/// parsing a snapshot written the same way the Control Agent's HealthPipeServer does.
/// </summary>
public class ControlAgentHealthClientTests
{
    [Fact]
    public async Task TryGetHealthAsync_NoServerListening_ReturnsNullWithinTimeout()
    {
        var options = new WatchdogOptions
        {
            HealthPipeName = $"CloudOrc.Tests.NoServer.{Guid.NewGuid():N}",
            HealthCheckTimeoutSeconds = 1
        };
        var client = new ControlAgentHealthClient(options, NullLogger<ControlAgentHealthClient>.Instance);

        var result = await client.TryGetHealthAsync(CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task TryGetHealthAsync_ServerRespondsWithSnapshot_ParsesItCorrectly()
    {
        var pipeName = $"CloudOrc.Tests.Server.{Guid.NewGuid():N}";
        var options = new WatchdogOptions { HealthPipeName = pipeName, HealthCheckTimeoutSeconds = 5 };
        var client = new ControlAgentHealthClient(options, NullLogger<ControlAgentHealthClient>.Instance);

        var snapshot = new ControlAgentHealthSnapshot
        {
            Status = "HEALTHY",
            DetectionWorkerAlive = true,
            ProcessingWorkerAlive = true,
            LastDetectionActivityAt = DateTimeOffset.UtcNow,
            LastProcessingActivityAt = DateTimeOffset.UtcNow,
            CurrentCommandId = "test-001",
            CurrentCommandStatus = null,
            ProcessedCount = 5,
            FailedCount = 1,
            GeneratedAt = DateTimeOffset.UtcNow
        };

        var serverTask = Task.Run(async () =>
        {
            await using var server = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await server.WaitForConnectionAsync();
            var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            await server.WriteAsync(payload);
            await server.FlushAsync();
            server.WaitForPipeDrain();
        });

        var result = await client.TryGetHealthAsync(CancellationToken.None);
        await serverTask;

        Assert.NotNull(result);
        Assert.Equal("HEALTHY", result!.Status);
        Assert.Equal("test-001", result.CurrentCommandId);
        Assert.Equal(5, result.ProcessedCount);
    }
}
