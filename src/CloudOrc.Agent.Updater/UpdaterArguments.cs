using System.Text.RegularExpressions;

namespace CloudOrc.Agent.Updater;

/// <summary>
/// Parsed, validated command line of the updater. Stable contract (Agent Core launches it exactly so):
/// <c>--installer P --sha256 H --expected-version V --install-dir D --work-dir W --result-file R
/// --update-id I [--health-timeout-seconds N] [--allow-unsigned]</c>.
/// </summary>
public sealed partial class UpdaterArguments
{
    public const int DefaultHealthTimeoutSeconds = 180;

    public required string InstallerPath { get; init; }
    public required string Sha256 { get; init; }
    public required string ExpectedVersion { get; init; }
    public required string InstallDirectory { get; init; }
    public required string WorkDirectory { get; init; }
    public required string ResultFile { get; init; }
    public required string UpdateId { get; init; }
    public int HealthTimeoutSeconds { get; init; } = DefaultHealthTimeoutSeconds;
    public bool AllowUnsigned { get; init; }

    public string RollbackDirectory => Path.Combine(WorkDirectory, "rollback", UpdateId);
    public string LogFile => Path.Combine(WorkDirectory, $"updater-{UpdateId}.log");
    public string InstallerLogFile => Path.Combine(WorkDirectory, $"installer-{UpdateId}.log");
    public string ControlAgentExe => Path.Combine(InstallDirectory, "ControlAgent", "CloudOrc.ControlAgent.exe");

    /// <summary>Parses args. Returns null and a human-readable error on any problem; never throws.</summary>
    public static UpdaterArguments? TryParse(string[] args, out string? error, out string? resultFileHint)
    {
        error = null;
        resultFileHint = null;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var allowUnsigned = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--allow-unsigned", StringComparison.OrdinalIgnoreCase))
            {
                allowUnsigned = true;
                continue;
            }

            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                error = $"Unexpected argument '{arg}'.";
                return null;
            }

            string name;
            string? value;
            var eq = arg.IndexOf('=');
            if (eq > 0)
            {
                name = arg[2..eq];
                value = arg[(eq + 1)..];
            }
            else
            {
                name = arg[2..];
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    error = $"Missing value for --{name}.";
                    return null;
                }

                value = args[++i];
            }

            if (!KnownValueOptions.Contains(name))
            {
                error = $"Unknown option --{name}.";
                return null;
            }

            if (values.ContainsKey(name))
            {
                error = $"Option --{name} was given more than once.";
                return null;
            }

            values[name] = value;
        }

        values.TryGetValue("result-file", out resultFileHint);

        foreach (var required in RequiredOptions)
        {
            if (!values.TryGetValue(required, out var v) || string.IsNullOrWhiteSpace(v))
            {
                error = $"Missing required option --{required}.";
                return null;
            }
        }

        var sha = values["sha256"].Trim();
        if (!Sha256Regex().IsMatch(sha))
        {
            error = "--sha256 must be 64 hexadecimal characters.";
            return null;
        }

        var updateId = values["update-id"].Trim();
        if (!UpdateIdRegex().IsMatch(updateId))
        {
            error = "--update-id may only contain letters, digits, '-', '_' and '.' (max 100 chars).";
            return null;
        }

        var version = values["expected-version"].Trim();
        if (!VersionRegex().IsMatch(version))
        {
            error = "--expected-version must look like X.Y.Z.";
            return null;
        }

        var timeout = DefaultHealthTimeoutSeconds;
        if (values.TryGetValue("health-timeout-seconds", out var timeoutText)
            && (!int.TryParse(timeoutText, out timeout) || timeout < 10 || timeout > 3600))
        {
            error = "--health-timeout-seconds must be an integer between 10 and 3600.";
            return null;
        }

        string installer, installDir, workDir, resultFile;
        try
        {
            installer = Path.GetFullPath(values["installer"]);
            installDir = Path.GetFullPath(values["install-dir"]).TrimEnd('\\', '/');
            workDir = Path.GetFullPath(values["work-dir"]).TrimEnd('\\', '/');
            resultFile = Path.GetFullPath(values["result-file"]);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"Invalid path: {ex.Message}";
            return null;
        }

        if (IsSameOrInside(workDir, installDir))
        {
            error = "--work-dir must not be inside --install-dir (the rollback backup would copy itself).";
            return null;
        }

        if (IsSameOrInside(installDir, workDir))
        {
            error = "--install-dir must not be inside --work-dir.";
            return null;
        }

        return new UpdaterArguments
        {
            InstallerPath = installer,
            Sha256 = sha.ToLowerInvariant(),
            ExpectedVersion = version,
            InstallDirectory = installDir,
            WorkDirectory = workDir,
            ResultFile = resultFile,
            UpdateId = updateId,
            HealthTimeoutSeconds = timeout,
            AllowUnsigned = allowUnsigned
        };
    }

    internal static bool IsSameOrInside(string candidate, string root)
    {
        var c = candidate.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var r = root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return c.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly string[] RequiredOptions =
        ["installer", "sha256", "expected-version", "install-dir", "work-dir", "result-file", "update-id"];

    private static readonly HashSet<string> KnownValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "installer", "sha256", "expected-version", "install-dir", "work-dir", "result-file", "update-id", "health-timeout-seconds"
    };

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Regex();

    [GeneratedRegex("^[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex UpdateIdRegex();

    [GeneratedRegex(@"^\d+\.\d+\.\d+([.-][0-9A-Za-z.-]+)?$")]
    private static partial Regex VersionRegex();
}
