using System.Net;
using CloudOrc.ControlAgent.Transport;

namespace CloudOrc.ControlAgent.Tests.Transport;

/// <summary>
/// §8: a tunnel or proxy answering for an offline backend (ngrok "endpoint offline" = 404 HTML)
/// must be retried; only a real CloudOrc API rejection (JSON body) may park an outbox row as dead.
/// Found in the live test: job events and the manifest were dropped while the tunnel was down.
/// </summary>
public sealed class IntermediaryResponseTests
{
    [Theory]
    [InlineData(404, "text/html", false)]
    [InlineData(404, null, false)]
    [InlineData(404, "application/json", true)]
    [InlineData(403, "text/plain", false)]
    public void ErrorsNotFromTheApi_AreIntermediary(int status, string? mediaType, bool ngrokHeader)
    {
        Assert.True(ApiResponse.LooksLikeIntermediary(status, mediaType, ngrokHeader));
    }

    [Theory]
    [InlineData(404, "application/json")]
    [InlineData(400, "application/json; charset=utf-8")]
    [InlineData(200, "text/html")]
    public void ApiErrorsAndSuccesses_AreNotIntermediary(int status, string mediaType)
    {
        Assert.False(ApiResponse.LooksLikeIntermediary(status, mediaType, hasProxyErrorHeader: false));
    }

    [Fact]
    public void TunnelOffline404_IsTransient_ButApi404_IsPermanent()
    {
        var tunnel = new ApiResponse(HttpStatusCode.NotFound, "<html>ERR_NGROK_3200</html>", null, null, FromIntermediary: true);
        var api = new ApiResponse(HttpStatusCode.NotFound, """{"error":{"code":"JOB_NOT_FOUND"}}""", null, null, FromIntermediary: false);

        Assert.True(tunnel.IsTransient);
        Assert.False(api.IsTransient);
    }
}
