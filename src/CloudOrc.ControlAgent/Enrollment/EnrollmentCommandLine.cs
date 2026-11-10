using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.Agent.Contracts.Versioning;
using CloudOrc.ControlAgent.Backup;
using CloudOrc.ControlAgent.Configuration;
using CloudOrc.ControlAgent.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudOrc.ControlAgent.Enrollment;

/// <summary>
/// Implements the one-shot enrollment CLI that runs BEFORE the normal Worker Service host is built:
/// <list type="bullet">
/// <item><c>enroll --token "ENR-..."</c> - managed VPS / zero-touch path (installer-injected, endpoint-bound token).</item>
/// <item><c>enroll --code ABCD-1234 [--api-base https://...]</c> - external / backup-only activation code (§4).</item>
/// </list>
/// On success it persists <see cref="Agent.Contracts.Enrollment.EnrolledAgentState"/> via
/// <see cref="EnrolledStateStore"/> and exits 0; on any failure it leaves no partial state behind
/// and exits non-zero, so the installer can detect the failure and refuse to report success.
/// </summary>
public static class EnrollmentCommandLine
{
    public const int ExitCodeInvalidArgumentsValue = 2;
    public const int ExitCodeEnrollmentFailedValue = 20;

    public static async Task<int> RunAsync(string[] args, ControlAgentOptions controlAgentOptions, ILoggerFactory loggerFactory) =>
        await RunAsync(args, controlAgentOptions, new ActivationOptions(), loggerFactory).ConfigureAwait(false);

    public static async Task<int> RunAsync(string[] args, ControlAgentOptions controlAgentOptions, ActivationOptions activationOptions, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("Enroll");

        var token = ParseArgument(args, "--token");
        var code = ParseArgument(args, "--code");
        if (token is null && code is null)
        {
            Console.Error.WriteLine("Usage: CloudOrc.ControlAgent.exe enroll --token \"ENR-...\"");
            Console.Error.WriteLine("   or: CloudOrc.ControlAgent.exe enroll --code ABCD-1234 [--api-base https://<control-plane>/]");
            return ExitCodeInvalidArgumentsValue;
        }

        Directory.CreateDirectory(controlAgentOptions.DataDirectory);

        var machineId = MachineIdResolver.Resolve(controlAgentOptions.DataDirectory, logger);
        var agentVersion = AgentVersionInfo.GetVersion(typeof(EnrollmentCommandLine).Assembly);
        var installationId = BackupPaths.GetOrCreateInstallationId(Path.Combine(controlAgentOptions.DataDirectory, "installation-id.txt"));
        var proof = OperatingSystem.IsWindows()
            ? EnrollmentFinalizer.PrepareDeviceProof(installationId, machineId, logger)
            : new DeviceProof(installationId, null, null);

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var store = new EnrolledStateStore(Options.Create(controlAgentOptions), loggerFactory.CreateLogger<EnrolledStateStore>());

        EnrollmentOutcome outcome;
        string method;
        string? apiBase = null;
        if (token is not null)
        {
            method = "token";
            Console.WriteLine("Contacting enrollment endpoint...");
            outcome = await new EnrollmentClient(httpClient)
                .EnrollAsync(token, machineId, Environment.MachineName, agentVersion, CancellationToken.None, proof)
                .ConfigureAwait(false);
        }
        else
        {
            method = "activation-code";
            apiBase = ParseArgument(args, "--api-base") ?? activationOptions.ApiBaseUrl;
            if (!TryParseApiBase(apiBase, activationOptions.DevelopmentAllowInsecureHttp, out var apiUri))
            {
                Console.Error.WriteLine("Activation needs the CloudOrc control-plane address: pass --api-base https://... or set Activation:ApiBaseUrl.");
                return ExitCodeInvalidArgumentsValue;
            }

            Console.WriteLine("Activating with CloudOrc...");
            outcome = await new ActivationCodeEnrollmentClient(httpClient).EnrollAsync(
                apiUri!,
                code!,
                new EndpointBinding
                {
                    MachineId = machineId,
                    MachineName = Environment.MachineName,
                    InstallationId = installationId,
                    OsVersion = Environment.OSVersion.VersionString,
                    DevicePublicKey = proof.DevicePublicKey,
                    CsrPem = proof.CsrPem
                },
                agentVersion,
                CancellationToken.None).ConfigureAwait(false);
            apiBase = apiUri!.ToString();
        }

        if (!outcome.IsSuccess)
        {
            Console.Error.WriteLine($"Enrollment failed: {outcome.Error}");
            logger.LogError("Enrollment failed: {Error}", outcome.Error);
            return ExitCodeEnrollmentFailedValue;
        }

        var response = outcome.Response!;
        if (OperatingSystem.IsWindows())
        {
            EnrollmentFinalizer.Persist(response, method, apiBase, store, logger);
        }

        Console.WriteLine($"Enrollment succeeded. AgentId={response.AgentId}, ServerId={response.ServerId}.");
        logger.LogInformation("Enrollment succeeded ({Method}). AgentId={AgentId}, ServerId={ServerId}, BackendUrl={BackendUrl}.",
            method, response.AgentId, response.ServerId, response.BackendUrl);
        return 0;
    }

    public static bool TryParseApiBase(string? value, bool allowInsecureHttp, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var withSlash = value.EndsWith('/') ? value : value + "/";
        if (!Uri.TryCreate(withSlash, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttps && !(allowInsecureHttp && parsed.Scheme == Uri.UriSchemeHttp))
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    private static string? ParseArgument(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                return args[i + 1];
            }

            if (args[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
            {
                return args[i][(name.Length + 1)..];
            }
        }

        return null;
    }
}
