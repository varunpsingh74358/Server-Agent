using CloudOrc.Agent.Contracts.Protocol;

namespace CloudOrc.ControlAgent.Tests.Protocol;

public class ProtocolJsonTests
{
    [Fact]
    public void TryReadMessageType_ValidMessage_ReturnsType()
    {
        var type = ProtocolJson.TryReadMessageType("""{"type":"PING"}""");

        Assert.Equal("PING", type);
    }

    [Fact]
    public void TryReadMessageType_MalformedJson_ReturnsNullWithoutThrowing()
    {
        var type = ProtocolJson.TryReadMessageType("{ this is not valid json");

        Assert.Null(type);
    }

    [Fact]
    public void TryReadMessageType_MissingTypeProperty_ReturnsNull()
    {
        var type = ProtocolJson.TryReadMessageType("""{"agentId":"a1"}""");

        Assert.Null(type);
    }

    [Fact]
    public void TryReadMessageType_EmptyString_ReturnsNullWithoutThrowing()
    {
        var type = ProtocolJson.TryReadMessageType(string.Empty);

        Assert.Null(type);
    }

    [Fact]
    public void TryReadMessageType_TotallyUnstructuredText_ReturnsNullWithoutThrowing()
    {
        var type = ProtocolJson.TryReadMessageType("hello world, not json at all");

        Assert.Null(type);
    }
}
