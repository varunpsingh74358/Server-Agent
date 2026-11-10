using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudOrc.Agent.Updater;

public static class UpdateOutcomes
{
    public const string Succeeded = "SUCCEEDED";
    public const string FailedRolledBack = "FAILED_ROLLED_BACK";
    public const string RollbackFailed = "ROLLBACK_FAILED";
    public const string VerificationFailed = "VERIFICATION_FAILED";
    public const string FailedBeforeInstall = "FAILED_BEFORE_INSTALL";
}

/// <summary>The result file Agent Core reads back after the update (camelCase JSON).</summary>
public sealed class UpdateResult
{
    public required string UpdateId { get; init; }
    public string? FromVersion { get; init; }
    public required string ToVersion { get; init; }
    public string? InstalledVersion { get; init; }
    public required string Outcome { get; init; }
    public string? Reason { get; init; }
    public int? InstallerExitCode { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }

    public int ToExitCode() => Outcome switch
    {
        UpdateOutcomes.Succeeded => 0,
        UpdateOutcomes.VerificationFailed => 10,
        UpdateOutcomes.FailedBeforeInstall => 11,
        UpdateOutcomes.FailedRolledBack => 12,
        UpdateOutcomes.RollbackFailed => 13,
        _ => 1
    };
}

public static class ResultWriter
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    /// <summary>Writes the result atomically: temp file in the same directory, then move over the target.</summary>
    public static void Write(string resultFile, UpdateResult result)
    {
        var directory = Path.GetDirectoryName(resultFile);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = resultFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(result, JsonOptions));
        File.Move(temp, resultFile, overwrite: true);
    }
}
