using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;

namespace CloudOrc.ControlAgent.Tests.Backend;

/// <summary>
/// Minimal real WebSocket server for testing <see cref="CloudOrc.ControlAgent.Backend.BackendConnectionService"/>
/// against actual socket behavior (connect, abrupt disconnect, reconnect) without pulling
/// in an ASP.NET Core TestHost dependency this solution doesn't otherwise use. Shared
/// across test files that need one or more independent loopback backends.
/// </summary>
internal sealed class LoopbackWebSocketServer : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly Lock _sync = new();
    private readonly List<TaskCompletionSource<WebSocket>> _waiters = [];

    public string Url { get; }

    private LoopbackWebSocketServer(HttpListener listener, string url)
    {
        _listener = listener;
        Url = url;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public static LoopbackWebSocketServer Start()
    {
        var port = GetFreeLoopbackPort();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        return new LoopbackWebSocketServer(listener, $"ws://127.0.0.1:{port}/agent");
    }

    public static int GetFreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public Task<WebSocket> WaitForNextConnectionAsync(TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<WebSocket>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            _waiters.Add(tcs);
        }

        return tcs.Task.WaitAsync(timeout);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }

            if (!context.Request.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
                continue;
            }

            var wsContext = await context.AcceptWebSocketAsync(subProtocol: null).ConfigureAwait(false);

            TaskCompletionSource<WebSocket>? waiter;
            lock (_sync)
            {
                waiter = _waiters.Count > 0 ? _waiters[0] : null;
                if (waiter is not null)
                {
                    _waiters.RemoveAt(0);
                }
            }

            waiter?.TrySetResult(wsContext.WebSocket);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        _listener.Close();
        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch
        {
            // Best-effort shutdown only.
        }
    }
}
