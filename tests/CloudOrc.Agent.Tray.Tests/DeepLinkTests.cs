using System.Text;
using CloudOrc.Agent.Tray;

namespace CloudOrc.Agent.Tray.Tests;

public class DeepLinkTests
{
    [Theory]
    [InlineData("cloudorc://activate?code=abcd-1234", "ABCD-1234")]
    [InlineData("\"cloudorc://activate/?code=XY7Z9Q\"", "XY7Z9Q")]
    [InlineData("CLOUDORC://ACTIVATE?CODE=abcdef12", "ABCDEF12")]
    public void ValidActivationLinks(string link, string expected) => Assert.Equal(expected, DeepLink.TryGetActivationCode(link));

    [Theory]
    [InlineData("https://activate?code=ABCD1234")]
    [InlineData("cloudorc://update?code=ABCD1234")]
    [InlineData("cloudorc://activate/evil?code=ABCD1234")]
    [InlineData("cloudorc://activate?code=ABCD1234&apiBase=https://evil.example")]
    [InlineData("cloudorc://activate?code=ABCD1234&code=EFGH5678")]
    [InlineData("cloudorc://activate?code=<script>")]
    [InlineData("cloudorc://activate")]
    [InlineData(null)]
    public void AnythingElse_IsIgnored(string? link) => Assert.Null(DeepLink.TryGetActivationCode(link));

    [Fact]
    public void WithoutUserApproval_NothingIsSentToTheService()
    {
        var connected = false;
        var client = new StatusPipeClient(_ => { connected = true; return Task.FromResult<Stream>(new MemoryStream()); });

        var exit = DeepLink.RunActivation("ABCD1234", client, (_, _) => false, (_, _, _) => { });

        Assert.Equal(1, exit);
        Assert.False(connected);
    }

    [Fact]
    public void Approved_SendsActivateWithTheCode_AndReportsSuccess()
    {
        var request = new MemoryStream();
        var duplex = new FakeDuplex(request, """{"ok":true,"state":"CONNECTING","deviceName":"d","agentVersion":"1","diagnosticsId":"CO-1","canReconnect":false}""" + "\n");
        var client = new StatusPipeClient(_ => Task.FromResult<Stream>(duplex));
        string? shown = null;

        var exit = DeepLink.RunActivation("ABCD1234", client, (_, _) => true, (_, text, error) => shown = error ? "ERR:" + text : text);

        Assert.Equal(0, exit);
        Assert.Contains("\"action\":\"activate\"", Encoding.UTF8.GetString(request.ToArray()));
        Assert.Contains("\"code\":\"ABCD1234\"", Encoding.UTF8.GetString(request.ToArray()));
        Assert.Contains("activated", shown);
    }

    private sealed class FakeDuplex(MemoryStream written, string reply) : Stream
    {
        private readonly MemoryStream _reply = new(Encoding.UTF8.GetBytes(reply));
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _reply.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => written.Write(buffer, offset, count);
    }
}
