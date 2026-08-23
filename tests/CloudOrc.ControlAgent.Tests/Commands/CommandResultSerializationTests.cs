using System.Text.Json;
using CloudOrc.Agent.Contracts.Commands;

namespace CloudOrc.ControlAgent.Tests.Commands;

public class CommandResultSerializationTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Serialize_SuccessResult_UsesStringStatusAndEmptyErrorIsNull()
    {
        var result = new CommandResult
        {
            CommandId = "test-001",
            Status = CommandStatus.Success,
            StartedAt = DateTimeOffset.Parse("2026-08-22T10:00:00Z"),
            CompletedAt = DateTimeOffset.Parse("2026-08-22T10:00:02Z"),
            DurationMilliseconds = 2000,
            Output = ["Saturday, August 22, 2026 10:00:00 AM"],
            Error = null
        };

        var json = JsonSerializer.Serialize(result, Options);

        Assert.Contains("\"status\":\"Success\"", json);
        Assert.Contains("\"error\":null", json);
        Assert.Contains("\"commandId\":\"test-001\"", json);
    }

    [Fact]
    public void RoundTrip_PreservesAllFields()
    {
        var original = new CommandResult
        {
            CommandId = "test-003",
            Status = CommandStatus.Failed,
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow.AddMilliseconds(100),
            DurationMilliseconds = 100,
            Output = [],
            Error = "Service 'DefinitelyDoesNotExist' was not found."
        };

        var json = JsonSerializer.Serialize(original, Options);
        var roundTripped = JsonSerializer.Deserialize<CommandResult>(json, Options);

        Assert.NotNull(roundTripped);
        Assert.Equal(original.CommandId, roundTripped!.CommandId);
        Assert.Equal(original.Status, roundTripped.Status);
        Assert.Equal(original.Error, roundTripped.Error);
        Assert.Empty(roundTripped.Output);
    }

    [Theory]
    [InlineData(CommandStatus.Queued)]
    [InlineData(CommandStatus.Running)]
    [InlineData(CommandStatus.Success)]
    [InlineData(CommandStatus.Failed)]
    [InlineData(CommandStatus.Timeout)]
    [InlineData(CommandStatus.Cancelled)]
    public void AllStatuses_RoundTripThroughJson(CommandStatus status)
    {
        var result = new CommandResult
        {
            CommandId = "id",
            Status = status,
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            DurationMilliseconds = 0
        };

        var json = JsonSerializer.Serialize(result, Options);
        var roundTripped = JsonSerializer.Deserialize<CommandResult>(json, Options);

        Assert.Equal(status, roundTripped!.Status);
    }
}
