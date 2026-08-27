using System.Net;
using CloudOrc.Agent.Contracts.Enrollment;
using CloudOrc.AgentTestServer;
using CloudOrc.AgentTestServer.Enrollment;

// ============================================================================
// DEVELOPMENT / TESTING ONLY - this is a local stand-in for the real CloudOrc
// backend, used to exercise the Control Agent's WSS connection before wiring
// it up to the real backend. It has no authentication. It binds to
// localhost/loopback by default; binding to a LAN-reachable address (e.g.
// 0.0.0.0) for cross-machine development testing requires explicitly opting
// in via TestServer.AllowNonLoopbackBinding - see docs/BACKEND_WEBSOCKET_TESTING.md.
// ============================================================================

var builder = WebApplication.CreateBuilder(args);

var port = builder.Configuration.GetValue<int?>("TestServer:Port") ?? 5299;
var bindAddress = builder.Configuration.GetValue<string?>("TestServer:BindAddress");
if (string.IsNullOrWhiteSpace(bindAddress))
{
    bindAddress = "localhost";
}
var allowNonLoopbackBinding = builder.Configuration.GetValue<bool?>("TestServer:AllowNonLoopbackBinding") ?? false;

var isLoopbackBind =
    bindAddress.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
    bindAddress == "127.0.0.1" ||
    bindAddress == "::1";

// Fail fast on an unsafe bind - never silently listen beyond loopback unless the
// operator has explicitly opted into cross-machine development testing. Same
// safety pattern as BackendConnection.DevelopmentAllowInsecureWs on the Control Agent.
if (!isLoopbackBind && !allowNonLoopbackBinding)
{
    throw new InvalidOperationException(
        $"TestServer.BindAddress ('{bindAddress}') is not loopback-only, but " +
        $"TestServer.AllowNonLoopbackBinding is false. This test server has NO " +
        $"authentication - only bind it beyond loopback for LAN DEVELOPMENT TESTING " +
        $"with TestServer.AllowNonLoopbackBinding explicitly set to true. Refusing to start.");
}

builder.WebHost.UseUrls($"http://{bindAddress}:{port}");

builder.Logging.ClearProviders();

builder.Services.AddSingleton<AgentSession>();
builder.Services.AddSingleton<EnrollmentTokenStore>();
builder.Services.AddSingleton<CredentialStore>();

var app = builder.Build();

// The enrollment endpoint this server issues in every token it hands out - "resolving
// the environment" for this dev/test stand-in just means "point back at myself", exactly
// as its WSS endpoint already does for BackendUrl below.
var enrollmentUrl = $"http://{bindAddress}:{port}/api/enroll";

app.UseWebSockets();

app.MapGet("/", () => "CloudOrc Agent Test Server - DEVELOPMENT TESTING ONLY. Connect a WebSocket to /agent.");

app.Map("/agent", async (HttpContext context, AgentSession session, CredentialStore credentials, CancellationToken cancellationToken) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    // Authenticated (enrolled) agents present "Authorization: Bearer <credential>" on the
    // handshake. Absent entirely is still accepted, to preserve the existing
    // pre-enrollment local dev/test flow (dotnet run --BackendConnection:Enabled=true...
    // with no enrollment at all) - this tool is DEV/TEST ONLY, so that leniency is
    // deliberate and would never be appropriate in a real backend.
    var authHeader = context.Request.Headers.Authorization.ToString();
    if (!string.IsNullOrEmpty(authHeader))
    {
        const string bearerPrefix = "Bearer ";
        if (!authHeader.StartsWith(bearerPrefix, StringComparison.Ordinal) ||
            !credentials.IsValid(authHeader[bearerPrefix.Length..]))
        {
            Console.WriteLine("[test-server] Rejected WebSocket handshake: invalid or revoked credential.");
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await AgentConnectionHandler.RunAsync(socket, session, cancellationToken);
});

// ----------------------------------------------------------------------------------
// Enrollment API - DEVELOPMENT/TEST ONLY reference implementation. A real backend's
// enrollment service would look identical in contract (same request/response shapes,
// same token semantics) but be backed by a real database, real credential rotation, and
// real revocation/audit - see docs/ENROLLMENT.md for exactly what production needs on
// top of this reference shape.
// ----------------------------------------------------------------------------------

app.MapPost("/api/enrollment-tokens", (IssueEnrollmentTokenRequest? request, EnrollmentTokenStore tokens) =>
{
    IPAddress? expectedIp = null;
    if (!string.IsNullOrWhiteSpace(request?.ExpectedIpAddress))
    {
        if (!IPAddress.TryParse(request.ExpectedIpAddress, out expectedIp))
        {
            return Results.BadRequest(new { error = $"ExpectedIpAddress '{request.ExpectedIpAddress}' is not a valid IP address." });
        }
    }

    var validFor = TimeSpan.FromSeconds(request?.ValidForSeconds ?? 900);
    var token = tokens.IssueToken(enrollmentUrl, validFor, expectedIp);
    Console.WriteLine(expectedIp is null
        ? $"[test-server] Issued a new enrollment token (valid for {validFor.TotalSeconds:F0}s, not IP-bound)."
        : $"[test-server] Issued a new enrollment token (valid for {validFor.TotalSeconds:F0}s, bound to {expectedIp}).");
    return Results.Ok(new { token });
});

app.MapPost("/api/enrollment-tokens/revoke", (RevokeEnrollmentTokenRequest request, EnrollmentTokenStore tokens) =>
{
    var revoked = tokens.RevokeByToken(request.Token);
    return revoked
        ? Results.Ok(new { revoked = true })
        : Results.BadRequest(new { revoked = false, error = "Token not found, or already used/expired/revoked." });
});

app.MapPost("/api/credentials/revoke", (RevokeCredentialRequest request, CredentialStore credentials) =>
{
    var revoked = credentials.Revoke(request.Credential);
    return revoked
        ? Results.Ok(new { revoked = true })
        : Results.BadRequest(new { revoked = false, error = "Credential not found." });
});

app.MapPost("/api/enroll", (EnrollmentRequest request, HttpContext context, EnrollmentTokenStore tokens, CredentialStore credentials) =>
{
    var validation = tokens.ValidateAndConsume(request.Secret, context.Connection.RemoteIpAddress);
    if (!validation.IsValid)
    {
        Console.WriteLine($"[test-server] Enrollment rejected for machine '{request.MachineName}': {validation.Error}");
        return Results.BadRequest(new { error = validation.Error });
    }

    var agentId = $"agent-{Guid.NewGuid():N}"[..14];
    const string serverId = "local-test-server";
    var credential = credentials.IssueCredential(agentId);
    var backendUrl = $"ws://{bindAddress}:{port}/agent";

    Console.WriteLine($"[test-server] Enrolled new agent: agentId={agentId}, machineName={request.MachineName}, machineId={request.MachineId}.");

    return Results.Ok(new EnrollmentResponse
    {
        AgentId = agentId,
        ServerId = serverId,
        BackendUrl = backendUrl,
        Credential = credential
    });
});

// Scriptable alternative to typing "send ..." at the console - useful for automated
// local testing. Same DEVELOPMENT-TESTING-ONLY, localhost-only, no-auth tool as the rest
// of this server; not a general management API.
app.MapPost("/send", async (SendCommandRequest request, AgentSession session, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Script))
    {
        return Results.BadRequest(new { error = "script is required" });
    }

    var commandId = request.CommandId ?? $"test-{Guid.NewGuid():N}"[..13];
    var timeoutSeconds = request.TimeoutSeconds ?? 30;

    var sent = await session.SendCommandAsync(commandId, request.Script, timeoutSeconds, cancellationToken);
    return sent
        ? Results.Ok(new { commandId, sent = true })
        : Results.Conflict(new { commandId, sent = false, error = "No agent is currently connected." });
});

Console.WriteLine("======================================================================");
Console.WriteLine(" CloudOrc Agent Test Server - DEVELOPMENT ONLY");
Console.WriteLine(" This is NOT the real CloudOrc backend and has no authentication.");
Console.WriteLine($" Listening on ws://{bindAddress}:{port}/agent" + (isLoopbackBind ? " (loopback only)" : " (LAN-reachable - DEVELOPMENT ONLY, no authentication)"));
if (!isLoopbackBind)
{
    Console.WriteLine(" WARNING: bound beyond loopback for cross-machine development testing.");
    Console.WriteLine(" Do not expose this address beyond a trusted development network.");
}
Console.WriteLine("======================================================================");

var agentSession = app.Services.GetRequiredService<AgentSession>();
var consoleLoop = new ConsoleCommandLoop(agentSession);

using var lifetimeCts = new CancellationTokenSource();

// Console.ReadLine() cannot be reliably cancelled mid-read. Running the input loop on a
// background thread means that if it's still blocked waiting for a line when the host
// shuts down (Ctrl+C, or the host stopping for any other reason), the process can still
// exit cleanly - a background thread never keeps the process alive.
var consoleThread = new Thread(() =>
{
    var explicitExitRequested = false;
    try
    {
        explicitExitRequested = consoleLoop.RunAsync(lifetimeCts.Token).GetAwaiter().GetResult();
    }
    catch (OperationCanceledException)
    {
        // Expected during shutdown.
    }

    if (explicitExitRequested && !lifetimeCts.IsCancellationRequested)
    {
        Console.WriteLine("[test-server] Shutting down...");
        lifetimeCts.Cancel();
    }
})
{
    IsBackground = true,
    Name = "console-command-loop"
};
consoleThread.Start();

await app.RunAsync(lifetimeCts.Token);
