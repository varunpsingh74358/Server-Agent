using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.Agent.Contracts.Enrollment;
using CloudOrc.Agent.Contracts.Identity;
using CloudOrc.ControlAgent.Configuration;
using CloudOrc.ControlAgent.Enrollment;
using CloudOrc.ControlAgent.Transport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudOrc.ControlAgent.Tests.Transport;

public class CertificateRotationDueTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NotDue_EarlyInLifetime() => Assert.False(CertificateRotationService.IsDue(Start, Start.AddDays(90), Start.AddDays(10)));

    [Fact]
    public void Due_AfterTwoThirdsOfLifetime() => Assert.True(CertificateRotationService.IsDue(Start, Start.AddDays(90), Start.AddDays(61)));

    [Fact]
    public void Due_WhenLessThanFourteenDaysRemain() => Assert.True(CertificateRotationService.IsDue(Start, Start.AddDays(365), Start.AddDays(352)));
}

/// <summary>Real rotation against a loopback "CA" backend, using the real machine certificate store and CNG.</summary>
public sealed class CertificateRotationIntegrationTests : IDisposable
{
    private readonly string _keyName = "CloudOrc.Test.DeviceKey." + Guid.NewGuid().ToString("N");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cloudorc-cert-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _thumbprints = [];
    private readonly HttpListener _listener = new();
    private readonly ECDsa _caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly X509Certificate2 _ca;
    private string? _receivedProofCheck;

    public CertificateRotationIntegrationTests()
    {
        var caRequest = new CertificateRequest("CN=CloudOrc Test CA", _caKey, HashAlgorithmName.SHA256);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        _ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    public void Dispose()
    {
        _listener.Close();
        using (var store = new X509Store(StoreName.My, StoreLocation.LocalMachine))
        {
            store.Open(OpenFlags.ReadWrite);
            foreach (var tp in _thumbprints)
            {
                foreach (var c in store.Certificates.Find(X509FindType.FindByThumbprint, tp, false))
                {
                    store.Remove(c);
                }
            }
        }

        if (CngKey.Exists(_keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.MachineKey))
        {
            using var key = CngKey.Open(_keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.MachineKey);
            key.Delete();
        }

        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string Issue(string csrPem, TimeSpan validFor, DateTimeOffset? notBefore = null)
    {
        var request = CertificateRequest.LoadSigningRequestPem(csrPem, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions);
        var nb = notBefore ?? DateTimeOffset.UtcNow.AddMinutes(-5);
        using var issued = request.Create(_ca, nb, nb + validFor, RandomNumberGenerator.GetBytes(12));
        return issued.ExportCertificatePem();
    }

    [Fact]
    public async Task DueCertificate_IsRotated_WithDeviceProof_AndOldOneRemoved()
    {
        var deviceKey = new DeviceKeyStore(_keyName);
        var devicePublicKey = deviceKey.GetPublicKeySpki();

        // Initial certificate: 10-day lifetime -> already inside the 14-day renewal window.
        var oldThumbprint = deviceKey.InstallClientCertificate(Issue(deviceKey.CreateCsrPem("machine-1"), TimeSpan.FromDays(10)));
        _thumbprints.Add(oldThumbprint);

        var controlOptions = Options.Create(new ControlAgentOptions { DataDirectory = _dir });
        var store = new EnrolledStateStore(controlOptions, NullLogger<EnrolledStateStore>.Instance);
        store.Save(new EnrolledAgentState
        {
            AgentId = "agent-cert", ServerId = "srv", BackendUrl = "wss://unused.example.test/agent", Credential = "cred",
            EnrolledAtUtc = DateTimeOffset.UtcNow, ClientCertificateThumbprint = oldThumbprint, CommandSigningPublicKey = "keep-me"
        });

        // Loopback backend acting as the CA: verifies the device proof, signs the CSR.
        var port = FreePort();
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Start();
        _ = Task.Run(async () =>
        {
            var ctx = await _listener.GetContextAsync();
            var body = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
            var req = JsonSerializer.Deserialize<CertificateRenewRequest>(body, BackendApiClient.SnakeCaseJson)!;
            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(Convert.FromBase64String(devicePublicKey), out _);
            var payload = CertificateProof.BuildPayload(req.EndpointId, req.Nonce, req.Timestamp,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(req.CsrPem))));
            var proofOk = verifier.VerifyData(Encoding.UTF8.GetBytes(payload), Convert.FromBase64String(req.Proof), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            _receivedProofCheck = $"{proofOk}|{req.CurrentThumbprint}";
            var json = JsonSerializer.Serialize(new CertificateRenewResponse { ClientCertificatePem = Issue(req.CsrPem, TimeSpan.FromDays(90)) }, BackendApiClient.SnakeCaseJson);
            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        });

        var identity = new AgentIdentity { AgentId = "agent-cert", ServerId = "srv", MachineId = "machine-1", MachineName = "n", AgentVersion = "1", Credential = "cred" };
        using var api = new BackendApiClient(
            Options.Create(new BackupOptions { ApiBaseUrl = null }),
            Options.Create(new BackendConnectionOptions { Url = $"ws://localhost:{port}/agent", DevelopmentAllowInsecureWs = true }),
            store, new AgentCredentialProvider(identity, store), TimeProvider.System, NullLogger<BackendApiClient>.Instance);
        var rotation = new CertificateRotationService(api, identity, store, deviceKey, TimeProvider.System, NullLogger<CertificateRotationService>.Instance);

        var outcome = await rotation.RotateIfDueAsync(force: false, CancellationToken.None);

        Assert.Equal(RotationOutcome.Rotated, outcome);
        Assert.Equal($"True|{oldThumbprint}", _receivedProofCheck);

        var state = store.TryLoad()!;
        _thumbprints.Add(state.ClientCertificateThumbprint!);
        Assert.NotEqual(oldThumbprint, state.ClientCertificateThumbprint);
        Assert.Equal("keep-me", state.CommandSigningPublicKey); // other persisted fields survive
        Assert.Null(DeviceKeyStore.FindClientCertificate(oldThumbprint)); // old cert removed
        using var fresh = DeviceKeyStore.FindClientCertificate(state.ClientCertificateThumbprint);
        Assert.NotNull(fresh);
        Assert.True(fresh!.HasPrivateKey);

        // Fresh 90-day certificate: nothing to do on the next check.
        Assert.Equal(RotationOutcome.NotNeeded, await rotation.RotateIfDueAsync(force: false, CancellationToken.None));
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
