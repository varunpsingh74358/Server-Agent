using System.Text.Json;
using CloudOrc.Agent.Contracts.Commands;
using CloudOrc.Agent.Contracts.Protocol;

namespace CloudOrc.ControlAgent.Tests.Protocol;

public class ProtocolMessageSerializationTests
{
    [Fact]
    public void HelloMessage_SerializesWithCorrectTypeAndFields()
    {
        var hello = new HelloMessage
        {
            AgentId = "agent-1",
            ServerId = "server-1",
            MachineId = "machine-guid",
            MachineName = "MYHOST",
            AgentVersion = "1.0.0"
        };

        var json = JsonSerializer.Serialize(hello, ProtocolJson.Options);

        Assert.Equal(ProtocolMessageTypes.Hello, ProtocolJson.TryReadMessageType(json));
        Assert.Contains("\"agentId\":\"agent-1\"", json);
        Assert.Contains("\"machineId\":\"machine-guid\"", json);
    }

    [Fact]
    public void HeartbeatMessage_RoundTripsWithNullCurrentCommand()
    {
        var heartbeat = new HeartbeatMessage
        {
            AgentId = "agent-1",
            ServerId = "server-1",
            Status = "HEALTHY",
            WorkerAlive = true,
            CurrentCommandId = null,
            CurrentCommandStatus = null,
            LastActivityAt = DateTimeOffset.UtcNow
        };

        var json = JsonSerializer.Serialize(heartbeat, ProtocolJson.Options);
        var roundTripped = JsonSerializer.Deserialize<HeartbeatMessage>(json, ProtocolJson.Options);

        Assert.Equal(ProtocolMessageTypes.Heartbeat, ProtocolJson.TryReadMessageType(json));
        Assert.NotNull(roundTripped);
        Assert.True(roundTripped!.WorkerAlive);
        Assert.Null(roundTripped.CurrentCommandId);
    }

    [Fact]
    public void HeartbeatMessage_RoundTripsWithActiveCommand()
    {
        var heartbeat = new HeartbeatMessage
        {
            AgentId = "agent-1",
            ServerId = "server-1",
            Status = "HEALTHY",
            WorkerAlive = true,
            CurrentCommandId = "test-001",
            CurrentCommandStatus = CommandStatus.Running,
            LastActivityAt = DateTimeOffset.UtcNow
        };

        var json = JsonSerializer.Serialize(heartbeat, ProtocolJson.Options);
        var roundTripped = JsonSerializer.Deserialize<HeartbeatMessage>(json, ProtocolJson.Options);

        Assert.Equal("test-001", roundTripped!.CurrentCommandId);
        Assert.Equal(CommandStatus.Running, roundTripped.CurrentCommandStatus);
    }

    [Fact]
    public void TelemetryMessage_RoundTripsWithDisksAndOptionalCpu()
    {
        var telemetry = new TelemetryMessage
        {
            AgentId = "agent-1",
            ServerId = "server-1",
            Machine = new TelemetryMachineInfo { MachineName = "MYHOST", Os = "Windows Server 2019" },
            Cpu = new TelemetryCpuInfo { UsagePercent = 12.5 },
            Memory = new TelemetryMemoryInfo { TotalBytes = 1000, UsedBytes = 400, AvailableBytes = 600 },
            Disks = [new TelemetryDiskInfo { Name = "C:", TotalBytes = 500_000, UsedBytes = 200_000, FreeBytes = 300_000 }],
            UptimeSeconds = 12345
        };

        var json = JsonSerializer.Serialize(telemetry, ProtocolJson.Options);
        var roundTripped = JsonSerializer.Deserialize<TelemetryMessage>(json, ProtocolJson.Options);

        Assert.Equal(ProtocolMessageTypes.Telemetry, ProtocolJson.TryReadMessageType(json));
        Assert.Equal("MYHOST", roundTripped!.Machine.MachineName);
        Assert.Equal(12.5, roundTripped.Cpu!.UsagePercent);
        Assert.Single(roundTripped.Disks);
        Assert.Equal("C:", roundTripped.Disks[0].Name);
    }

    [Fact]
    public void TelemetryMessage_MissingCpuAndDisks_SerializesWithoutThrowing()
    {
        var telemetry = new TelemetryMessage
        {
            AgentId = "agent-1",
            ServerId = "server-1",
            Machine = new TelemetryMachineInfo { MachineName = "MYHOST" },
            Cpu = null,
            Memory = null,
            UptimeSeconds = 1
        };

        var json = JsonSerializer.Serialize(telemetry, ProtocolJson.Options);
        var roundTripped = JsonSerializer.Deserialize<TelemetryMessage>(json, ProtocolJson.Options);

        Assert.Null(roundTripped!.Cpu);
        Assert.Empty(roundTripped.Disks);
    }

    [Fact]
    public void CommandStatusMessage_RoundTrips()
    {
        var status = new CommandStatusMessage { CommandId = "test-001", CorrelationId = "corr-001", Status = CommandStatus.Running };

        var json = JsonSerializer.Serialize(status, ProtocolJson.Options);
        var roundTripped = JsonSerializer.Deserialize<CommandStatusMessage>(json, ProtocolJson.Options);

        Assert.Equal(ProtocolMessageTypes.CommandStatus, ProtocolJson.TryReadMessageType(json));
        Assert.Equal(CommandStatus.Running, roundTripped!.Status);
        Assert.Equal("corr-001", roundTripped.CorrelationId);
    }

    [Fact]
    public void CommandResultMessage_FromCommandResult_PreservesAllFields()
    {
        var result = new CommandResult
        {
            CommandId = "test-001",
            CorrelationId = "corr-001",
            Status = CommandStatus.Success,
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow.AddSeconds(1),
            DurationMilliseconds = 1000,
            Output = ["line1", "line2"],
            Error = null,
            ExitCode = 0
        };

        var message = CommandResultMessage.FromCommandResult(result);
        var json = JsonSerializer.Serialize(message, ProtocolJson.Options);
        var roundTripped = JsonSerializer.Deserialize<CommandResultMessage>(json, ProtocolJson.Options);

        Assert.Equal(ProtocolMessageTypes.CommandResult, ProtocolJson.TryReadMessageType(json));
        Assert.Equal(result.CommandId, roundTripped!.CommandId);
        Assert.Equal(result.CorrelationId, roundTripped.CorrelationId);
        Assert.Equal(result.Status, roundTripped.Status);
        Assert.Equal(2, roundTripped.Output.Count);
        Assert.Equal(0, roundTripped.ExitCode);
    }

    [Fact]
    public void CommandMessage_RoundTripsWithParametersAndCorrelationId()
    {
        var command = new CommandMessage
        {
            CommandId = "test-001",
            CorrelationId = "corr-001",
            CommandType = "powershell-exec",
            Parameters = new CommandParameters { Script = "Get-Date", TimeoutSeconds = 30 }
        };

        var json = JsonSerializer.Serialize(command, ProtocolJson.Options);
        var roundTripped = JsonSerializer.Deserialize<CommandMessage>(json, ProtocolJson.Options);

        Assert.Equal(ProtocolMessageTypes.Command, ProtocolJson.TryReadMessageType(json));
        Assert.Contains("\"commandType\":\"powershell-exec\"", json);
        Assert.Contains("\"parameters\":{", json);
        Assert.Equal("test-001", roundTripped!.CommandId);
        Assert.Equal("corr-001", roundTripped.CorrelationId);
        Assert.Equal("Get-Date", roundTripped.Parameters.Script);
        Assert.Equal(30, roundTripped.Parameters.TimeoutSeconds);
    }

    [Fact]
    public void PingMessage_HasCorrectType()
    {
        var json = JsonSerializer.Serialize(new PingMessage(), ProtocolJson.Options);

        Assert.Equal(ProtocolMessageTypes.Ping, ProtocolJson.TryReadMessageType(json));
    }

    [Fact]
    public void ErrorMessage_RoundTrips()
    {
        var error = new ErrorMessage { Message = "bad command", RelatedCommandId = "test-001", CorrelationId = "corr-001" };

        var json = JsonSerializer.Serialize(error, ProtocolJson.Options);
        var roundTripped = JsonSerializer.Deserialize<ErrorMessage>(json, ProtocolJson.Options);

        Assert.Equal(ProtocolMessageTypes.Error, ProtocolJson.TryReadMessageType(json));
        Assert.Equal("test-001", roundTripped!.RelatedCommandId);
        Assert.Equal("corr-001", roundTripped.CorrelationId);
    }
}
