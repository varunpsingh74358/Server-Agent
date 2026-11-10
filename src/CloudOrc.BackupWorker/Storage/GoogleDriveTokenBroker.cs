using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudOrc.Agent.Contracts.Backup;

namespace CloudOrc.BackupWorker.Storage;

/// <summary>
/// Loopback OAuth "token endpoint" for rclone's Google Drive backend.
///
/// Why: the customer's Drive refresh token must stay on the server (§10 "scoped storage access
/// with bounded exposure"); the agent only ever receives a ~1 h access token in its grant. A
/// multi-hour backup outlives that token. rclone lets the token URL be overridden (token_url), so
/// rclone is given a dummy refresh token and this broker as its token URL: when rclone refreshes,
/// the broker returns the CURRENT grant's access token, asking Agent Core (→ backend
/// POST /api/v1/storage/grants) for a new grant first if the current one is about to expire.
///
/// Security: bound to 127.0.0.1 only, on a random port, under a random unguessable path, and the
/// caller must present the per-job random client secret. Lives only as long as the job.
/// MUST be validated in the M0 PoC (CO-DEV-BKP-001 ADR-02) against the pinned rclone version.
/// </summary>
public sealed class GoogleDriveTokenBroker : IAsyncDisposable
{
    public const string DummyRefreshToken = "cloudorc-broker";
    public const string ClientId = "cloudorc-agent-broker";

    private readonly HttpListener _listener = new();
    private readonly Func<CancellationToken, Task<GoogleDriveGrant?>> _currentToken;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public GoogleDriveTokenBroker(Func<CancellationToken, Task<GoogleDriveGrant?>> currentToken)
    {
        _currentToken = currentToken;
        ClientSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var pathSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var port = FreeLoopbackPort();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/{pathSecret}/");
        _listener.Start();
        TokenUrl = $"http://127.0.0.1:{port}/{pathSecret}/token";
        _loop = Task.Run(LoopAsync);
    }

    /// <summary>Per-job random value rclone presents as its OAuth client secret. SECRET.</summary>
    public string ClientSecret { get; }

    public string TokenUrl { get; }

    public static string TokenJson(GoogleDriveGrant grant) =>
        JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["access_token"] = grant.AccessToken,
            ["token_type"] = "Bearer",
            ["refresh_token"] = DummyRefreshToken,
            ["expiry"] = grant.TokenExpiresAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
        });

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            if (context.Request.HttpMethod != "POST" || !context.Request.Url!.AbsolutePath.EndsWith("/token", StringComparison.Ordinal)
                || !IPAddress.IsLoopback(context.Request.RemoteEndPoint.Address))
            {
                await RespondAsync(context, 404, "{\"error\":\"not_found\"}").ConfigureAwait(false);
                return;
            }

            string body;
            using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            if (!PresentsSecret(context.Request.Headers["Authorization"], body))
            {
                await RespondAsync(context, 401, "{\"error\":\"invalid_client\"}").ConfigureAwait(false);
                return;
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(100));
            var grant = await _currentToken(timeout.Token).ConfigureAwait(false);
            if (grant is null)
            {
                await RespondAsync(context, 400, "{\"error\":\"invalid_grant\",\"error_description\":\"storage grant could not be renewed\"}").ConfigureAwait(false);
                return;
            }

            var expiresIn = Math.Max(0, (int)(grant.TokenExpiresAt - DateTimeOffset.UtcNow).TotalSeconds);
            var json = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["access_token"] = grant.AccessToken,
                ["token_type"] = "Bearer",
                ["expires_in"] = expiresIn,
                ["refresh_token"] = DummyRefreshToken
            });
            await RespondAsync(context, 200, json).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            try
            {
                context.Response.Abort();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>Accepts the client secret either as HTTP Basic auth or as a form field (both OAuth2 styles).</summary>
    internal bool PresentsSecret(string? authorization, string formBody)
    {
        if (authorization is not null && authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authorization[6..].Trim()));
                var colon = decoded.IndexOf(':');
                if (colon > 0 && FixedTimeEquals(Uri.UnescapeDataString(decoded[(colon + 1)..]), ClientSecret))
                {
                    return true;
                }
            }
            catch (FormatException)
            {
            }
        }

        foreach (var pair in formBody.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2 && kv[0] == "client_secret" && FixedTimeEquals(Uri.UnescapeDataString(kv[1].Replace('+', ' ')), ClientSecret))
            {
                return true;
            }
        }

        return false;
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static async Task RespondAsync(HttpListenerContext context, int status, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        context.Response.Headers["Cache-Control"] = "no-store";
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        context.Response.Close();
    }

    private static int FreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        _cts.Dispose();
    }
}
