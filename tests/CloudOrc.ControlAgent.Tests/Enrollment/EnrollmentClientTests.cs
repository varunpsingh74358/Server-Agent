using System.Net;
using System.Net.Http.Json;
using CloudOrc.Agent.Contracts.Enrollment;
using CloudOrc.ControlAgent.Enrollment;

namespace CloudOrc.ControlAgent.Tests.Enrollment;

public class EnrollmentClientTests
{
    private static readonly string ValidToken = EnrollmentToken.Encode("https://enroll.example.test/api/enroll", "the-secret");

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private static EnrollmentClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new HttpClient(new StubHandler(respond)));

    [Fact]
    public async Task EnrollAsync_InvalidTokenFormat_FailsWithoutMakingAnHttpCall()
    {
        var called = false;
        var client = CreateClient(_ => { called = true; return new HttpResponseMessage(HttpStatusCode.OK); });

        var outcome = await client.EnrollAsync("not-a-real-token", "machine-1", "MACHINE1", "1.0.0", CancellationToken.None);

        Assert.False(outcome.IsSuccess);
        Assert.False(called);
        Assert.Contains("not in a recognized format", outcome.Error);
    }

    [Fact]
    public async Task EnrollAsync_ValidTokenAndSuccessfulResponse_ReturnsEnrollmentResponse()
    {
        var client = CreateClient(request =>
        {
            Assert.Equal("https://enroll.example.test/api/enroll", request.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new EnrollmentResponse
                {
                    AgentId = "agent-1",
                    ServerId = "server-1",
                    BackendUrl = "wss://backend.example.test/agent",
                    Credential = "issued-credential"
                })
            };
        });

        var outcome = await client.EnrollAsync(ValidToken, "machine-1", "MACHINE1", "1.0.0", CancellationToken.None);

        Assert.True(outcome.IsSuccess);
        Assert.Equal("agent-1", outcome.Response!.AgentId);
        Assert.Equal("wss://backend.example.test/agent", outcome.Response.BackendUrl);
        Assert.Equal("issued-credential", outcome.Response.Credential);
    }

    [Fact]
    public async Task EnrollAsync_ServerRejectsToken_ReturnsFailureWithBodyIncluded()
    {
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = JsonContent.Create(new { error = "Enrollment token has already been used." })
        });

        var outcome = await client.EnrollAsync(ValidToken, "machine-1", "MACHINE1", "1.0.0", CancellationToken.None);

        Assert.False(outcome.IsSuccess);
        Assert.Contains("400", outcome.Error);
        Assert.Contains("already been used", outcome.Error);
    }

    [Fact]
    public async Task EnrollAsync_ResponseFieldsPresentButEmpty_ReturnsFailure()
    {
        // C#'s `required` keyword makes JSON with a field MISSING throw during
        // deserialization (covered by the "unreadable response" branch below) - this
        // covers the other invalid shape: fields present but blank, which deserializes
        // successfully and must be caught by the post-deserialize completeness check.
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { agentId = "", serverId = "", backendUrl = "", credential = "" })
        });

        var outcome = await client.EnrollAsync(ValidToken, "machine-1", "MACHINE1", "1.0.0", CancellationToken.None);

        Assert.False(outcome.IsSuccess);
        Assert.Contains("incomplete", outcome.Error);
    }

    [Fact]
    public async Task EnrollAsync_ResponseMissingRequiredFields_ReturnsFailureAsUnreadable()
    {
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { agentId = "agent-1" }) // missing backendUrl/credential/serverId
        });

        var outcome = await client.EnrollAsync(ValidToken, "machine-1", "MACHINE1", "1.0.0", CancellationToken.None);

        Assert.False(outcome.IsSuccess);
        Assert.Contains("unreadable", outcome.Error);
    }

    [Fact]
    public async Task EnrollAsync_NetworkFailure_ReturnsFailureWithoutThrowing()
    {
        var client = new EnrollmentClient(new HttpClient(new ThrowingHandler()));

        var outcome = await client.EnrollAsync(ValidToken, "machine-1", "MACHINE1", "1.0.0", CancellationToken.None);

        Assert.False(outcome.IsSuccess);
        Assert.Contains("Could not reach", outcome.Error);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("simulated network failure");
    }
}
