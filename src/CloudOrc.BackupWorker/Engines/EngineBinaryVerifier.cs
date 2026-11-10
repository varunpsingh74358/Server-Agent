using System.Security.Cryptography;
using System.Text.Json;

namespace CloudOrc.BackupWorker.Engines;

public sealed record EngineBinary(string Name, string Version, string Path);

public sealed class EngineBinaryException(string reasonCode, string message) : Exception(message)
{
    public string ReasonCode { get; } = reasonCode;
}

/// <summary>
/// Fail-closed integrity check for engine binaries (§15 "Tampered installer/update/engine binary →
/// Signature verification fails closed"). engines.manifest.json is delivered by the signed
/// installer and pins each binary's SHA-256; a mismatch, a missing file or a manifest that does not
/// list the engine means the job does not run.
/// </summary>
public static class EngineBinaryVerifier
{
    public const string ManifestFileName = "engines.manifest.json";

    public static EngineBinary Resolve(string engineDirectory, string engineName)
    {
        var manifestPath = System.IO.Path.Combine(engineDirectory, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new EngineBinaryException(Agent.Contracts.Backup.BackupReasonCodes.EngineBinaryMissing, "Engine manifest is missing.");
        }

        JsonElement entry = default;
        var found = false;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            foreach (var e in doc.RootElement.GetProperty("engines").EnumerateArray())
            {
                if (string.Equals(e.GetProperty("name").GetString(), engineName, StringComparison.OrdinalIgnoreCase))
                {
                    entry = e.Clone();
                    found = true;
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new EngineBinaryException(Agent.Contracts.Backup.BackupReasonCodes.EngineBinaryTampered, "Engine manifest is unreadable.");
        }

        if (!found)
        {
            throw new EngineBinaryException(Agent.Contracts.Backup.BackupReasonCodes.EngineBinaryMissing, $"Engine '{engineName}' is not installed.");
        }

        var file = entry.GetProperty("file").GetString() ?? "";
        var expected = entry.GetProperty("sha256").GetString() ?? "";
        var version = entry.TryGetProperty("version", out var v) ? v.GetString() ?? "unknown" : "unknown";

        // The file name must be a plain name inside the engine directory.
        if (file.Length == 0 || file != System.IO.Path.GetFileName(file))
        {
            throw new EngineBinaryException(Agent.Contracts.Backup.BackupReasonCodes.EngineBinaryTampered, "Engine manifest has an invalid file name.");
        }

        var path = System.IO.Path.Combine(engineDirectory, file);
        if (!File.Exists(path))
        {
            throw new EngineBinaryException(Agent.Contracts.Backup.BackupReasonCodes.EngineBinaryMissing, $"Engine binary for '{engineName}' is missing.");
        }

        if (!HashMatches(path, expected))
        {
            throw new EngineBinaryException(Agent.Contracts.Backup.BackupReasonCodes.EngineBinaryTampered,
                $"Engine binary for '{engineName}' does not match its pinned SHA-256 - refusing to run it.");
        }

        return new EngineBinary(engineName, version, path);
    }

    public static bool HashMatches(string path, string expectedHex)
    {
        if (expectedHex.Length != 64)
        {
            return false;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        return string.Equals(actual, expectedHex, StringComparison.OrdinalIgnoreCase);
    }
}
