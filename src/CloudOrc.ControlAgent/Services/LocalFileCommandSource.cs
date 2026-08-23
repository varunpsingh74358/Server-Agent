using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CloudOrc.Agent.Contracts.Abstractions;
using CloudOrc.Agent.Contracts.Commands;
using CloudOrc.ControlAgent.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudOrc.ControlAgent.Services;

/// <summary>
/// Local-development <see cref="ICommandSource"/> that watches a directory of JSON files.
/// Detection, JSON/CommandId validation, and duplicate protection all happen here, before
/// a job is ever handed to the generic PowerShell execution engine - so replacing this
/// with a future SecureWebSocketCommandSource never touches execution code.
///
/// Delivery semantics (documented honestly, see docs/ARCHITECTURE.md): this provides
/// at-least-once, not exactly-once, execution. A command file is claimed by atomically
/// moving it from commands\ to processing\ before it is read, which prevents the same
/// file from being picked up twice while the agent is running. Duplicate protection by
/// CommandId (checked against results\ and an in-memory "claimed" set) additionally
/// prevents re-running a command whose result already exists. However, if the process
/// crashes while a command is actively executing (after being claimed but before a result
/// is written), the file recovered from processing\ on the next startup will be
/// re-executed, because no result exists yet to prove it already ran.
/// </summary>
public sealed class LocalFileCommandSource(
    IOptions<ControlAgentOptions> options,
    ILogger<LocalFileCommandSource> logger) : ICommandSource
{
    private readonly ControlAgentOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, byte> _claimedCommandIds = new(StringComparer.OrdinalIgnoreCase);

    public async IAsyncEnumerable<CommandJob> GetCommandsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        RecoverOrphanedProcessingFiles();

        var pollInterval = TimeSpan.FromSeconds(Math.Max(1, _options.PollIntervalSeconds));

        while (!cancellationToken.IsCancellationRequested)
        {
            List<string> candidateFiles;
            try
            {
                candidateFiles = [.. Directory.EnumerateFiles(_options.CommandsDirectory, "*.json").OrderBy(f => f)];
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Transient error listing commands directory {Directory}; will retry.", _options.CommandsDirectory);
                candidateFiles = [];
            }

            foreach (var file in candidateFiles)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    yield break;
                }

                CommandJob? job = null;
                try
                {
                    job = TryClaimAndValidate(file);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Unexpected error while claiming command file {File}; skipping it this cycle.", file);
                }

                if (job is not null)
                {
                    yield return job;
                }
            }

            try
            {
                await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
        }
    }

    public Task AcknowledgeAsync(CommandJob job, bool succeeded, CancellationToken cancellationToken)
    {
        var destinationDirectory = succeeded ? _options.CompletedDirectory : _options.FailedDirectory;
        var destination = Path.Combine(destinationDirectory, $"{job.Request.CommandId}.json");

        try
        {
            if (File.Exists(job.SourceReference))
            {
                MoveWithOverwrite(job.SourceReference, destination);
            }
        }
        catch (IOException ex)
        {
            logger.LogError(ex, "Failed to move processed command file for CommandId {CommandId} into {Destination}.", job.Request.CommandId, destinationDirectory);
        }

        return Task.CompletedTask;
    }

    private CommandJob? TryClaimAndValidate(string commandFilePath)
    {
        if (!IsFileStable(commandFilePath))
        {
            return null;
        }

        var fileName = Path.GetFileName(commandFilePath);
        var claimedPath = Path.Combine(_options.ProcessingDirectory, fileName);

        try
        {
            File.Move(commandFilePath, claimedPath);
        }
        catch (IOException)
        {
            // Another cycle (or, in a multi-instance setup, another process) already claimed it.
            return null;
        }

        CommandRequest? request;
        try
        {
            var json = File.ReadAllText(claimedPath);
            request = JsonSerializer.Deserialize<CommandRequest>(json, JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            logger.LogError(ex, "Command file {File} contains invalid JSON and cannot be parsed. Moving to failed\\.", fileName);
            MoveWithOverwrite(claimedPath, Path.Combine(_options.FailedDirectory, fileName));
            return null;
        }

        var validation = CommandRequestValidator.Validate(request, _options.Validation);
        if (!validation.IsValid)
        {
            logger.LogWarning("Command file {File} failed validation: {Error}", fileName, validation.Error);
            HandleInvalidCommand(request, claimedPath, fileName, validation.Error!);
            return null;
        }

        var commandId = request!.CommandId;

        if (IsDuplicate(commandId))
        {
            logger.LogWarning("CommandId {CommandId} is a duplicate (already completed/failed or claimed this session); skipping re-execution.", commandId);
            var duplicateName = $"{commandId}.duplicate-{DateTimeOffset.UtcNow.Ticks}.json";
            MoveWithOverwrite(claimedPath, Path.Combine(_options.FailedDirectory, duplicateName));
            return null;
        }

        _claimedCommandIds.TryAdd(commandId, 0);

        var renamedProcessingPath = Path.Combine(_options.ProcessingDirectory, $"{commandId}.json");
        if (!string.Equals(claimedPath, renamedProcessingPath, StringComparison.OrdinalIgnoreCase))
        {
            MoveWithOverwrite(claimedPath, renamedProcessingPath);
        }

        return new CommandJob
        {
            Request = request,
            EffectiveTimeoutSeconds = validation.EffectiveTimeoutSeconds,
            SourceReference = renamedProcessingPath,
            OriginSource = this
        };
    }

    private void HandleInvalidCommand(CommandRequest? request, string claimedPath, string fileName, string error)
    {
        if (request is not null && !string.IsNullOrWhiteSpace(request.CommandId))
        {
            var resultPath = Path.Combine(_options.ResultsDirectory, $"{request.CommandId}.result.json");
            var failureResult = new CommandResult
            {
                CommandId = request.CommandId,
                Status = CommandStatus.Failed,
                StartedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow,
                DurationMilliseconds = 0,
                Output = [],
                Error = error
            };
            WriteResultAtomically(resultPath, failureResult);
            MoveWithOverwrite(claimedPath, Path.Combine(_options.FailedDirectory, $"{request.CommandId}.json"));
        }
        else
        {
            MoveWithOverwrite(claimedPath, Path.Combine(_options.FailedDirectory, fileName));
        }
    }

    private bool IsDuplicate(string commandId)
    {
        if (_claimedCommandIds.ContainsKey(commandId))
        {
            return true;
        }

        var resultPath = Path.Combine(_options.ResultsDirectory, $"{commandId}.result.json");
        var completedPath = Path.Combine(_options.CompletedDirectory, $"{commandId}.json");
        var failedPath = Path.Combine(_options.FailedDirectory, $"{commandId}.json");

        return File.Exists(resultPath) || File.Exists(completedPath) || File.Exists(failedPath);
    }

    private bool IsFileStable(string filePath)
    {
        try
        {
            var lastWriteUtc = File.GetLastWriteTimeUtc(filePath);
            var age = DateTime.UtcNow - lastWriteUtc;
            return age.TotalMilliseconds >= _options.FileStabilityMilliseconds;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private void RecoverOrphanedProcessingFiles()
    {
        if (!Directory.Exists(_options.ProcessingDirectory))
        {
            return;
        }

        var orphaned = Directory.EnumerateFiles(_options.ProcessingDirectory, "*.json").ToList();
        foreach (var file in orphaned)
        {
            var destination = Path.Combine(_options.CommandsDirectory, Path.GetFileName(file));
            logger.LogWarning(
                "Recovered orphaned command file {File} left behind by a previous run; it will be re-detected and re-executed. " +
                "This is expected at-least-once behavior, not a bug - see docs/ARCHITECTURE.md.",
                file);
            MoveWithOverwrite(file, destination);
        }
    }

    private void MoveWithOverwrite(string source, string destination)
    {
        try
        {
            File.Move(source, destination, overwrite: true);
        }
        catch (IOException ex)
        {
            logger.LogError(ex, "Failed to move {Source} to {Destination}.", source, destination);
        }
    }

    private static void WriteResultAtomically(string destinationPath, CommandResult result)
    {
        var tempPath = destinationPath + ".tmp";
        var json = JsonSerializer.Serialize(result, JsonOptions);
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, destinationPath, overwrite: true);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
