using System.Text.Json;
using CloudOrc.ControlAgent.Enrollment;

namespace CloudOrc.ControlAgent.Configuration;

/// <summary>
/// activation.json in the agent's data directory: the persisted "Activation" section (control-plane
/// address + optional portal link). Kept outside {app} so it survives upgrades, and outside
/// appsettings.json so the installer never has to edit JSON. Contains no secrets.
/// </summary>
public static class ActivationSettingsFile
{
    public const string FileName = "activation.json";

    public static string PathFor(string dataDirectory) => Path.Combine(dataDirectory, FileName);

    public static int RunCommandLine(string[] args, string dataDirectory)
    {
        var apiBase = Arg(args, "--api-base");
        var portal = Arg(args, "--portal-url");
        if (!EnrollmentCommandLine.TryParseApiBase(apiBase, allowInsecureHttp: false, out var apiUri))
        {
            Console.Error.WriteLine("Usage: CloudOrc.ControlAgent.exe set-activation --api-base https://<control-plane>/ [--portal-url https://<portal>/]");
            return EnrollmentCommandLine.ExitCodeInvalidArgumentsValue;
        }

        if (portal is not null && (!Uri.TryCreate(portal, UriKind.Absolute, out var portalUri) || portalUri.Scheme != Uri.UriSchemeHttps))
        {
            Console.Error.WriteLine("--portal-url must be an absolute https:// address.");
            return EnrollmentCommandLine.ExitCodeInvalidArgumentsValue;
        }

        Write(dataDirectory, apiUri!.ToString(), portal);
        Console.WriteLine("Activation endpoint saved.");
        return 0;
    }

    public static void Write(string dataDirectory, string apiBaseUrl, string? portalUrl)
    {
        Directory.CreateDirectory(dataDirectory);
        var json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [ActivationOptions.SectionName] = new Dictionary<string, string?>
            {
                [nameof(ActivationOptions.ApiBaseUrl)] = apiBaseUrl,
                [nameof(ActivationOptions.PortalUrl)] = portalUrl
            }
        }, new JsonSerializerOptions { WriteIndented = true });

        var path = PathFor(dataDirectory);
        File.WriteAllText(path + ".tmp", json);
        File.Move(path + ".tmp", path, overwrite: true);
    }

    private static string? Arg(string[] args, string name)
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
