using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using CloudOrc.ControlAgent.Backend;

namespace CloudOrc.ControlAgent.Tests.Backend;

public class ConnectionFailureClassifierTests
{
    [Fact]
    public void Classify_ConnectTimeout_ReportsTimeoutSpecifically()
    {
        var result = ConnectionFailureClassifier.Classify(new OperationCanceledException(), wasConnectTimeout: true);

        Assert.Contains("Timeout", result);
        Assert.Contains("firewall", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Classify_ConnectionRefused_ReportsConnectionRefusedSpecifically()
    {
        var socketEx = new SocketException((int)SocketError.ConnectionRefused);
        var httpEx = new HttpRequestException("failed", socketEx);
        var wsEx = new WebSocketException("failed", httpEx);

        var result = ConnectionFailureClassifier.Classify(wsEx, wasConnectTimeout: false);

        Assert.Contains("Connection refused", result);
    }

    [Fact]
    public void Classify_DnsFailure_ReportsDnsSpecifically()
    {
        var socketEx = new SocketException((int)SocketError.HostNotFound);

        var result = ConnectionFailureClassifier.Classify(socketEx, wasConnectTimeout: false);

        Assert.Contains("DNS", result);
    }

    [Fact]
    public void Classify_TlsAuthenticationException_ReportsTlsSpecifically()
    {
        var tlsEx = new AuthenticationException("The remote certificate is invalid.");

        var result = ConnectionFailureClassifier.Classify(tlsEx, wasConnectTimeout: false);

        Assert.Contains("TLS", result);
        Assert.Contains("certificate", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Classify_UnauthorizedHandshake_ReportsAuthenticationFailureSpecifically()
    {
        var wsEx = new WebSocketException("The server returned status code '401' when status code '101' was expected.");

        var result = ConnectionFailureClassifier.Classify(wsEx, wasConnectTimeout: false);

        Assert.Contains("Authentication failure", result);
        Assert.Contains("revoked", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Classify_GenericWebSocketFailure_IncludesTheOriginalMessage()
    {
        var wsEx = new WebSocketException("some unexpected handshake problem");

        var result = ConnectionFailureClassifier.Classify(wsEx, wasConnectTimeout: false);

        Assert.Contains("some unexpected handshake problem", result);
    }

    [Fact]
    public void Classify_UnrecognizedException_FallsBackToTypeAndMessage()
    {
        var ex = new InvalidOperationException("something else entirely");

        var result = ConnectionFailureClassifier.Classify(ex, wasConnectTimeout: false);

        Assert.Contains(nameof(InvalidOperationException), result);
        Assert.Contains("something else entirely", result);
    }
}
