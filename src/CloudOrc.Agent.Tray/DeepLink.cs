namespace CloudOrc.Agent.Tray;

/// <summary>
/// §4 "User installs once, enters code or approves deep link". The portal's "Add Device" can
/// offer a link <c>cloudorc://activate?code=ABCD-1234</c>. Clicking it starts the tray with that
/// URL; the user must explicitly APPROVE before anything happens (a web page must never be able to
/// activate a device silently), and the service additionally refuses activation on a device that
/// is already activated and connected.
/// </summary>
public static class DeepLink
{
    public const string Scheme = "cloudorc";

    /// <summary>Returns the normalized activation code from an activation deep link, or null if it is not one.</summary>
    public static string? TryGetActivationCode(string? argument)
    {
        if (string.IsNullOrWhiteSpace(argument) || argument.Length > 200
            || !Uri.TryCreate(argument.Trim().Trim('"'), UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, "activate", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath is not ("" or "/"))
        {
            return null;
        }

        string? code = null;
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length != 2)
            {
                return null;
            }

            if (!string.Equals(kv[0], "code", StringComparison.OrdinalIgnoreCase) || code is not null)
            {
                return null; // only a single "code" parameter is accepted
            }

            code = Uri.UnescapeDataString(kv[1]);
        }

        return ActivationCodeFormat.TryNormalize(code, out var normalized) ? normalized : null;
    }

    /// <summary>Asks the user, then activates through the service. Returns the process exit code.</summary>
    public static int RunActivation(string code, StatusPipeClient client, Func<string, string, bool> confirm, Action<string, string, bool> notify)
    {
        var question =
            $"Activate this computer ({Environment.MachineName}) with CloudOrc using code {code}?\n\n" +
            "Only continue if you just created this code in the CloudOrc portal yourself.";
        if (!confirm("CloudOrc - approve device activation", question))
        {
            return 1;
        }

        var result = client.SendAsync(new Protocol.LocalStatusRequest { Action = Protocol.LocalStatusProtocol.ActionActivate, Code = code }, CancellationToken.None)
            .GetAwaiter().GetResult();

        if (!result.IsAvailable)
        {
            notify("CloudOrc", result.UnavailableReason ?? "The CloudOrc Agent service is not running.", true);
            return 2;
        }

        var response = result.Response!;
        if (!response.Ok)
        {
            notify("CloudOrc", response.Error ?? "Activation failed.", true);
            return 3;
        }

        notify("CloudOrc", "This device is activated. It will connect to CloudOrc in a moment.", false);
        return 0;
    }
}
