using System.Text.Json;
using CloudOrc.Agent.Contracts.Abstractions;
using CloudOrc.Agent.Contracts.Commands;
using CloudOrc.ControlAgent.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudOrc.ControlAgent.Services;

/// <summary>
/// Writes a <see cref="CommandResult"/> to results\{commandId}.result.json. Writes go to
/// a temporary file first and are then renamed into place, so a reader can never observe
/// a partially-written result file.
/// </summary>
public sealed class LocalFileResultSink(
    IOptions<ControlAgentOptions> options,
    ILogger<LocalFileResultSink> logger) : ICommandResultSink
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly ControlAgentOptions _options = options.Value;

    public Task WriteAsync(CommandResult result, CancellationToken cancellationToken)
    {
        var finalPath = Path.Combine(_options.ResultsDirectory, $"{result.CommandId}.result.json");
        var tempPath = finalPath + ".tmp";

        try
        {
            var json = JsonSerializer.Serialize(result, JsonOptions);
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, finalPath, overwrite: true);
        }
        catch (IOException ex)
        {
            logger.LogError(ex, "Failed to write result file for CommandId {CommandId}.", result.CommandId);
        }

        return Task.CompletedTask;
    }
}
