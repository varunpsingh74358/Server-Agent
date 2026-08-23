using CloudOrc.Agent.Contracts.Enrollment;
using CloudOrc.ControlAgent.Configuration;
using CloudOrc.ControlAgent.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudOrc.ControlAgent.Enrollment;

/// <summary>
/// Implements <c>CloudOrc.ControlAgent.exe enroll --token "ENR-..."</c> - a one-shot mode
/// invoked by the installer (or manually, for re-enrollment) that runs BEFORE the normal
/// Worker Service host is built. On success it persists <see cref="EnrolledAgentState"/>
/// via <see cref="EnrolledStateStore"/> and exits 0; on any failure it leaves no partial
/// state behind and exits non-zero, so the installer can detect the failure and refuse to
/// report success.
/// </summary>
public static class EnrollmentCommandLine
{
    public const int ExitCodeInvalidArgumentsValue = 2;
    public const int ExitCodeEnrollmentFailedValue = 20;

    public static async Task<int> RunAsync(string[] args, ControlAgentOptions controlAgentOptions, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("Enroll");

        var token = ParseTokenArgument(args);
        if (token is null)
        {
            Console.Error.WriteLine("Usage: CloudOrc.ControlAgent.exe enroll --token \"ENR-...\"");
            return ExitCodeInvalidArgumentsValue;
        }

        Directory.CreateDirectory(controlAgentOptions.DataDirectory);

        var machineId = MachineIdResolver.Resolve(controlAgentOptions.DataDirectory, logger);
        var agentVersion = typeof(EnrollmentCommandLine).Assembly.GetName().Version?.ToString() ?? "0.0.0";

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var client = new EnrollmentClient(httpClient);

        Console.WriteLine("Contacting enrollment endpoint...");
        var outcome = await client.EnrollAsync(token, machineId, Environment.MachineName, agentVersion, CancellationToken.None).ConfigureAwait(false);

        if (!outcome.IsSuccess)
        {
            Console.Error.WriteLine($"Enrollment failed: {outcome.Error}");
            logger.LogError("Enrollment failed: {Error}", outcome.Error);
            return ExitCodeEnrollmentFailedValue;
        }

        var response = outcome.Response!;
        var state = new EnrolledAgentState
        {
            AgentId = response.AgentId,
            ServerId = response.ServerId,
            BackendUrl = response.BackendUrl,
            Credential = response.Credential,
            EnrolledAtUtc = DateTimeOffset.UtcNow
        };

        var store = new EnrolledStateStore(Options.Create(controlAgentOptions), loggerFactory.CreateLogger<EnrolledStateStore>());
        store.Save(state);

        Console.WriteLine($"Enrollment succeeded. AgentId={response.AgentId}, ServerId={response.ServerId}.");
        logger.LogInformation("Enrollment succeeded. AgentId={AgentId}, ServerId={ServerId}, BackendUrl={BackendUrl}.", response.AgentId, response.ServerId, response.BackendUrl);
        return 0;
    }

    private static string? ParseTokenArgument(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--token", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                return args[i + 1];
            }

            if (args[i].StartsWith("--token=", StringComparison.OrdinalIgnoreCase))
            {
                return args[i]["--token=".Length..];
            }
        }

        return null;
    }
}
