using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using CloudOrc.Agent.Tray.Protocol;

namespace CloudOrc.Agent.Tray;

/// <summary>Outcome of one request to Agent Core: either a response, or "service not reachable".</summary>
public sealed class PipeCallResult
{
    private PipeCallResult(LocalStatusResponse? response, string? unavailableReason)
    {
        Response = response;
        UnavailableReason = unavailableReason;
    }

    public LocalStatusResponse? Response { get; }

    /// <summary>Non-null when the agent service could not be reached or answered garbage.</summary>
    public string? UnavailableReason { get; }

    public bool IsAvailable => Response is not null;

    public static PipeCallResult FromResponse(LocalStatusResponse response) => new(response, null);

    public static PipeCallResult Unavailable(string reason) => new(null, reason);
}

/// <summary>
/// One-request-per-connection client for the Agent Core local status pipe: connect (2s
/// timeout), write one JSON line, read one JSON line, disconnect. The connection factory is
/// injectable so tests can run the exchange over an in-memory stream.
/// </summary>
public sealed class StatusPipeClient
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Plain status/retry/cancel calls should answer immediately.</summary>
    public static readonly TimeSpan DefaultExchangeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Activation makes the service call the enrollment endpoint, so allow longer.</summary>
    public static readonly TimeSpan ActivationExchangeTimeout = TimeSpan.FromSeconds(60);

    private const int MaxResponseCharacters = 64 * 1024;

    private readonly Func<CancellationToken, Task<Stream>> _connect;

    public StatusPipeClient(Func<CancellationToken, Task<Stream>> connect)
    {
        _connect = connect ?? throw new ArgumentNullException(nameof(connect));
    }

    public static StatusPipeClient CreateDefault() => CreateForPipe(LocalStatusProtocol.PipeName);

    public static StatusPipeClient CreateForPipe(string pipeName) => new(async cancellationToken =>
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    });

    public Task<PipeCallResult> GetStatusAsync(CancellationToken cancellationToken) =>
        SendAsync(new LocalStatusRequest { Action = LocalStatusProtocol.ActionStatus }, cancellationToken);

    public async Task<PipeCallResult> SendAsync(LocalStatusRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var exchangeTimeout = request.Action == LocalStatusProtocol.ActionActivate
            ? ActivationExchangeTimeout
            : DefaultExchangeTimeout;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(exchangeTimeout);

        try
        {
            await using var stream = await _connect(timeoutCts.Token).ConfigureAwait(false);

            var requestLine = JsonSerializer.Serialize(request, LocalStatusProtocol.JsonOptions) + "\n";
            var requestBytes = Encoding.UTF8.GetBytes(requestLine);
            await stream.WriteAsync(requestBytes, timeoutCts.Token).ConfigureAwait(false);
            await stream.FlushAsync(timeoutCts.Token).ConfigureAwait(false);

            using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            var line = await reader.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(line))
            {
                return PipeCallResult.Unavailable("The CloudOrc Agent service closed the connection without answering.");
            }

            if (line.Length > MaxResponseCharacters)
            {
                return PipeCallResult.Unavailable("The CloudOrc Agent service returned an oversized response.");
            }

            var response = JsonSerializer.Deserialize<LocalStatusResponse>(line, LocalStatusProtocol.JsonOptions);
            return response is null
                ? PipeCallResult.Unavailable("The CloudOrc Agent service returned an empty response.")
                : PipeCallResult.FromResponse(response);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            return PipeCallResult.Unavailable(TrayViewModel.ServiceNotRunningText);
        }
        catch (JsonException)
        {
            return PipeCallResult.Unavailable("The CloudOrc Agent service returned an unreadable response.");
        }
    }
}
