namespace CloudOrc.ControlAgent.Transport;

/// <summary>
/// Works out the HTTPS control-plane base URL. Priority: explicit config override → value issued
/// at enrollment → derived from the enrolled WebSocket URL (wss://host[:port]/agent →
/// https://host[:port]/). Nothing is hardcoded: every host comes from enrollment or config.
/// </summary>
public static class ControlPlaneAddress
{
    public static Uri? Resolve(string? configuredOverride, string? enrolledApiBaseUrl, string? webSocketUrl, bool allowInsecureHttp)
    {
        foreach (var candidate in new[] { configuredOverride, enrolledApiBaseUrl })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && Uri.TryCreate(EnsureTrailingSlash(candidate), UriKind.Absolute, out var explicitUri)
                && IsAcceptable(explicitUri, allowInsecureHttp))
            {
                return explicitUri;
            }
        }

        if (string.IsNullOrWhiteSpace(webSocketUrl) || !Uri.TryCreate(webSocketUrl, UriKind.Absolute, out var ws))
        {
            return null;
        }

        var scheme = ws.Scheme.ToLowerInvariant() switch
        {
            "wss" => Uri.UriSchemeHttps,
            "ws" => Uri.UriSchemeHttp,
            _ => null
        };

        if (scheme is null)
        {
            return null;
        }

        var derived = new UriBuilder(scheme, ws.Host, ws.IsDefaultPort ? -1 : ws.Port, "/").Uri;
        return IsAcceptable(derived, allowInsecureHttp) ? derived : null;
    }

    private static bool IsAcceptable(Uri uri, bool allowInsecureHttp) =>
        uri.Scheme == Uri.UriSchemeHttps || (allowInsecureHttp && uri.Scheme == Uri.UriSchemeHttp);

    private static string EnsureTrailingSlash(string value) => value.EndsWith('/') ? value : value + "/";
}
