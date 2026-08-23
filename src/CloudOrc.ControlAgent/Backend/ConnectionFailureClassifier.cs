using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;

namespace CloudOrc.ControlAgent.Backend;

/// <summary>
/// Turns a raw connection exception into one short, specific, actionable line instead of
/// a bare stack trace - so "Backend connection attempt failed" (previously the only log
/// line an operator had to go on) becomes something like "Connection timed out after 10s
/// - nothing responded at ws://host:port. Check the host/port and firewall rules." Pure
/// function, no I/O - easy to unit test against synthetic exceptions covering failure
/// modes that are impractical to reproduce on demand in a real network (DNS failure, TLS
/// failure), plus live-reproducible ones (timeout, connection refused).
/// </summary>
public static class ConnectionFailureClassifier
{
    public static string Classify(Exception exception, bool wasConnectTimeout)
    {
        if (wasConnectTimeout)
        {
            return $"Timeout - no response from the backend host/port within the configured connect timeout. " +
                   $"Check that the address is correct, the port is open, and no firewall is blocking outbound traffic.";
        }

        var authFailure = FindInChain<AuthenticationException>(exception);
        if (authFailure is not null)
        {
            return $"TLS/certificate validation failed ({authFailure.Message}). " +
                   $"Check the backend's certificate is valid and trusted by this machine.";
        }

        var socketFailure = FindInChain<SocketException>(exception);
        if (socketFailure is not null)
        {
            return socketFailure.SocketErrorCode switch
            {
                SocketError.HostNotFound or SocketError.TryAgain =>
                    "DNS resolution failed - the backend hostname could not be resolved. Check the address is correct and DNS is reachable.",
                SocketError.ConnectionRefused =>
                    "Connection refused - nothing is listening on that host/port, or a firewall is rejecting the connection.",
                SocketError.TimedOut or SocketError.HostUnreachable or SocketError.NetworkUnreachable =>
                    "Network unreachable/timed out at the socket level - check routing and firewall rules between this server and the backend.",
                _ => $"Network error ({socketFailure.SocketErrorCode}) while connecting to the backend."
            };
        }

        var webSocketFailure = FindInChain<WebSocketException>(exception);
        if (webSocketFailure is not null)
        {
            // ClientWebSocket surfaces a rejected handshake (e.g. HTTP 401/403 from an
            // enrollment-authentication check) as a WebSocketException whose message
            // typically includes the HTTP status text - no strongly-typed status code is
            // exposed on all target frameworks, so this is intentionally a message-text
            // match rather than a strongly-typed check.
            if (webSocketFailure.Message.Contains("401") || webSocketFailure.Message.Contains("403")
                || webSocketFailure.Message.Contains("Unauthorized") || webSocketFailure.Message.Contains("Forbidden"))
            {
                return "Authentication failure - the backend rejected the connection's credential (HTTP 401/403). " +
                       "The agent's enrolled credential may be invalid or have been revoked; re-enrollment may be required.";
            }

            return $"WebSocket handshake failed: {webSocketFailure.Message}";
        }

        return $"{exception.GetType().Name}: {exception.Message}";
    }

    private static T? FindInChain<T>(Exception? exception) where T : Exception
    {
        while (exception is not null)
        {
            if (exception is T match)
            {
                return match;
            }

            exception = exception.InnerException;
        }

        return null;
    }
}
