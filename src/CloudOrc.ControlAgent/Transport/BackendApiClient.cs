using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CloudOrc.ControlAgent.Configuration;
using CloudOrc.ControlAgent.Enrollment;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudOrc.ControlAgent.Transport;

/// <summary>Outcome of one control-plane call. Never contains the request body or credentials.</summary>
public sealed record ApiResponse(HttpStatusCode? StatusCode, string? Body, TimeSpan? RetryAfter, string? NetworkError)
{
    public bool IsSuccess => StatusCode is { } s && (int)s is >= 200 and < 300;

    /// <summary>409 on an idempotent write = "already have it" → treat as delivered.</summary>
    public bool IsDuplicate => StatusCode == HttpStatusCode.Conflict;

    public bool IsNetworkFailure => NetworkError is not null;

    /// <summary>Worth retrying later: network, timeout, 408, 425, 429, 5xx, and 401 (renewal may fix it).</summary>
    public bool IsTransient => IsNetworkFailure
        || StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or HttpStatusCode.Unauthorized or (HttpStatusCode)425
        || (StatusCode is { } s && (int)s >= 500);

    public T? Read<T>(JsonSerializerOptions options) where T : class =>
        string.IsNullOrWhiteSpace(Body) ? null : JsonSerializer.Deserialize<T>(Body, options);

    public string Describe() => NetworkError ?? $"HTTP {(int?)StatusCode}";
}

/// <summary>
/// HTTPS client for the control plane (§12 admission, grants, job events, session renewal).
/// Bearer = the current session credential; client certificate = the mTLS cert when present.
/// Bulk backup data NEVER goes through here (§3 "Large backup bytes bypass transactional APIs") -
/// only small JSON and the compressed file manifest.
/// </summary>
public sealed class BackendApiClient : IDisposable
{
    public static readonly JsonSerializerOptions SnakeCaseJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly AgentCredentialProvider _credentials;
    private readonly ILogger<BackendApiClient> _logger;
    private readonly TimeProvider _time;

    public BackendApiClient(
        IOptions<BackupOptions> backupOptions,
        IOptions<BackendConnectionOptions> connectionOptions,
        EnrolledStateStore enrolledStateStore,
        AgentCredentialProvider credentials,
        TimeProvider time,
        ILogger<BackendApiClient> logger)
    {
        _credentials = credentials;
        _logger = logger;
        _time = time;

        var enrolled = enrolledStateStore.TryLoad();
        BaseAddress = ControlPlaneAddress.Resolve(
            backupOptions.Value.ApiBaseUrl,
            enrolled?.ApiBaseUrl,
            connectionOptions.Value.Url,
            allowInsecureHttp: connectionOptions.Value.DevelopmentAllowInsecureWs);

        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectTimeout = TimeSpan.FromSeconds(15)
        };

        if (OperatingSystem.IsWindows() && enrolled?.ClientCertificateThumbprint is not null)
        {
            // Resolved at every TLS handshake (pooled connections recycle every 5 min), so a rotated
            // certificate takes over without restarting the agent.
            handler.SslOptions.LocalCertificateSelectionCallback = (_, _, _, _, _) =>
                DeviceKeyStore.FindClientCertificate(enrolledStateStore.TryLoad()?.ClientCertificateThumbprint)!;
        }

        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(5, backupOptions.Value.ControlPlaneTimeoutSeconds))
        };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CloudOrcAgent", Agent.Contracts.Versioning.AgentVersionInfo.GetVersion()));
    }

    /// <summary>Null when no usable HTTPS control-plane address is known (not enrolled / misconfigured).</summary>
    public Uri? BaseAddress { get; }

    public bool IsConfigured => BaseAddress is not null;

    public Task<ApiResponse> PostJsonAsync(string route, object body, CancellationToken cancellationToken) =>
        PostJsonStringAsync(route, JsonSerializer.Serialize(body, body.GetType(), SnakeCaseJson), cancellationToken);

    public Task<ApiResponse> PostJsonStringAsync(string route, string json, CancellationToken cancellationToken) =>
        SendAsync(route, () => new StringContent(json, Encoding.UTF8, "application/json"), cancellationToken);

    /// <summary>Streams a (pre-gzipped) file body - used for the recovery-point manifest.</summary>
    public Task<ApiResponse> PostFileAsync(string route, string filePath, string contentType, bool gzipEncoded, CancellationToken cancellationToken) =>
        SendAsync(route, () =>
        {
            var content = new StreamContent(new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan));
            content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            if (gzipEncoded)
            {
                content.Headers.ContentEncoding.Add("gzip");
            }

            return content;
        }, cancellationToken);

    private async Task<ApiResponse> SendAsync(string route, Func<HttpContent> contentFactory, CancellationToken cancellationToken)
    {
        if (BaseAddress is null)
        {
            return new ApiResponse(null, null, null, "Control-plane address is not configured (agent not enrolled?).");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseAddress, route));
        using var content = contentFactory();
        request.Content = content;

        var credential = _credentials.Current;
        if (!string.IsNullOrEmpty(credential))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        }

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (body.Length > 64 * 1024)
            {
                body = body[..(64 * 1024)];
            }

            return new ApiResponse(response.StatusCode, body, RetryAfter.FromResponse(response, _time.GetUtcNow()), null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ApiResponse(null, null, null, "Request timed out.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Control-plane request to {Route} failed.", route);
            return new ApiResponse(null, null, null, $"Network error: {ex.Message}");
        }
        catch (IOException ex)
        {
            return new ApiResponse(null, null, null, $"I/O error: {ex.Message}");
        }
    }

    public void Dispose() => _http.Dispose();
}
