using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.ControlAgent.Backend;
using CloudOrc.ControlAgent.Commands;
using CloudOrc.ControlAgent.DurableQueue;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudOrc.ControlAgent.Tests.Commands;

public sealed class CommandSignatureVerifierTests
{
    private readonly ECDsa _backendKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private string PublicKey => Convert.ToBase64String(_backendKey.ExportSubjectPublicKeyInfo());

    /// <summary>What the backend does: sign the canonical payload over the exact parameter JSON it sends.</summary>
    private string SignedCommand(string commandId, string type, string parametersJson, DateTimeOffset issuedAt, string? overrideParameters = null)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(parametersJson)));
        var payload = CommandSigning.BuildPayload(commandId, type, "1.0", issuedAt, hash);
        var signature = Convert.ToBase64String(_backendKey.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        return $$"""{"type":"COMMAND","commandId":"{{commandId}}","commandType":"{{type}}","commandVersion":"1.0","issuedAt":"{{issuedAt:O}}","signature":"{{signature}}","parameters":{{overrideParameters ?? parametersJson}}}""";
    }

    [Fact]
    public void ValidSignature_IsAccepted()
    {
        var verifier = new CommandSignatureVerifier(PublicKey, false, TimeProvider.System);
        Assert.True(verifier.Verify(SignedCommand("c1", BackupCommandTypes.Run, """{"jobId":"j1","policyId":"p1"}""", DateTimeOffset.UtcNow)).Ok);
    }

    [Fact]
    public void TamperedParameters_AreRejected()
    {
        var verifier = new CommandSignatureVerifier(PublicKey, false, TimeProvider.System);
        var json = SignedCommand("c1", BackupCommandTypes.RestoreExecute, """{"targetPath":"D:\\Restore"}""", DateTimeOffset.UtcNow,
            overrideParameters: """{"targetPath":"C:\\Windows"}""");
        Assert.Equal("SIGNATURE_INVALID", verifier.Verify(json).ReasonCode);
    }

    [Fact]
    public void StaleOrFutureCommands_AreRejected()
    {
        var verifier = new CommandSignatureVerifier(PublicKey, false, TimeProvider.System);
        Assert.Equal("SIGNATURE_EXPIRED", verifier.Verify(SignedCommand("c1", BackupCommandTypes.Run, "{}", DateTimeOffset.UtcNow.AddHours(-1))).ReasonCode);
        Assert.Equal("SIGNATURE_EXPIRED", verifier.Verify(SignedCommand("c1", BackupCommandTypes.Run, "{}", DateTimeOffset.UtcNow.AddHours(1))).ReasonCode);
    }

    [Fact]
    public void SignatureFromAnotherKey_IsRejected()
    {
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var verifier = new CommandSignatureVerifier(Convert.ToBase64String(attacker.ExportSubjectPublicKeyInfo()), false, TimeProvider.System);
        Assert.Equal("SIGNATURE_INVALID", verifier.Verify(SignedCommand("c1", BackupCommandTypes.Run, "{}", DateTimeOffset.UtcNow)).ReasonCode);
    }

    [Fact]
    public void Unsigned_IsRejectedOnceAKeyIsKnown_ButAllowedForLegacyAgentsWithoutOne()
    {
        const string unsigned = """{"type":"COMMAND","commandId":"c1","commandType":"backup.run","parameters":{}}""";
        Assert.Equal("SIGNATURE_MISSING", new CommandSignatureVerifier(PublicKey, false, TimeProvider.System).Verify(unsigned).ReasonCode);
        Assert.True(new CommandSignatureVerifier(null, false, TimeProvider.System).Verify(unsigned).Ok);
        Assert.Equal("SIGNATURE_KEY_UNAVAILABLE", new CommandSignatureVerifier(null, true, TimeProvider.System).Verify(unsigned).ReasonCode);
    }

    /// <summary>
    /// The CloudOrc backend stores commands in jsonb (keys reordered), so it ships the exact signed
    /// text as the string "parametersJson"; "parameters" may then differ in key order.
    /// </summary>
    private string SignedWithParametersJson(string commandId, string type, string signedText, string parametersObject, string? tamperedText = null)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signedText))).ToLowerInvariant();
        var issuedAt = DateTimeOffset.UtcNow;
        var payload = CommandSigning.BuildPayload(commandId, type, "1.0", issuedAt, hash);
        var signature = Convert.ToBase64String(_backendKey.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        var envelope = new Dictionary<string, object?>
        {
            ["type"] = "COMMAND", ["commandId"] = commandId, ["commandType"] = type, ["commandVersion"] = "1.0",
            ["issuedAt"] = issuedAt.ToString("O"), ["signature"] = signature,
            ["parameters"] = JsonDocument.Parse(parametersObject).RootElement,
            ["parametersJson"] = tamperedText ?? signedText
        };
        return JsonSerializer.Serialize(envelope);
    }

    [Fact]
    public void ParametersJson_IsWhatIsVerified_EvenWhenParametersKeysWereReordered()
    {
        var verifier = new CommandSignatureVerifier(PublicKey, false, TimeProvider.System);
        var json = SignedWithParametersJson("c2", BackupCommandTypes.Cancel, """{"jobId":"j1","reason":"user"}""", """{"reason":"user","jobId":"j1"}""");
        Assert.True(verifier.Verify(json).Ok);
    }

    [Fact]
    public void TamperedParametersJson_IsRejected()
    {
        var verifier = new CommandSignatureVerifier(PublicKey, false, TimeProvider.System);
        var json = SignedWithParametersJson("c3", BackupCommandTypes.RestoreExecute, """{"targetPath":"D:\\Restore"}""", """{"targetPath":"D:\\Restore"}""",
            tamperedText: """{"targetPath":"C:\\Windows"}""");
        Assert.Equal("SIGNATURE_INVALID", verifier.Verify(json).ReasonCode);
    }

    [Fact]
    public void Context_ParsesTheSignedParametersJson_NotTheParametersObject()
    {
        // A relay that rewrote "parameters" but not the signed text must not change what executes.
        var json = SignedWithParametersJson("c4", BackupCommandTypes.Cancel, """{"jobId":"signed-job"}""", """{"jobId":"other-job"}""");
        var context = new TypedCommandContext("c4", null, BackupCommandTypes.Cancel, "1.0", null, json);
        Assert.Equal("signed-job", context.Parameters<Dictionary<string, string>>()!["jobId"]);
    }

    [Fact]
    public void Context_FallsBackToParametersObject_ForEnvelopesWithoutParametersJson()
    {
        var json = SignedCommand("c5", BackupCommandTypes.Cancel, """{"jobId":"j9"}""", DateTimeOffset.UtcNow);
        var context = new TypedCommandContext("c5", null, BackupCommandTypes.Cancel, "1.0", null, json);
        Assert.Equal("j9", context.Parameters<Dictionary<string, string>>()!["jobId"]);
    }

    [Theory]
    [InlineData(4401, System.Net.HttpStatusCode.Unauthorized)]
    [InlineData(4403, System.Net.HttpStatusCode.Forbidden)]
    [InlineData(1013, System.Net.HttpStatusCode.ServiceUnavailable)]
    public void BackendCloseCodes_MapToRetryPolicyStatuses(int closeCode, System.Net.HttpStatusCode expected)
    {
        Assert.Equal(expected, BackendConnectionService.MapCloseCode(closeCode));
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(1001)]
    [InlineData(4000)]
    [InlineData(null)]
    public void OrdinaryCloseCodes_AreNotAuthorizationVerdicts(int? closeCode)
    {
        Assert.Null(BackendConnectionService.MapCloseCode(closeCode));
    }

    [Fact]
    public async Task Dispatcher_RejectsUnsignedCommand_BeforeItReachesTheHandlerOrInbox()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cloudorc-sig-" + Guid.NewGuid().ToString("N"));
        using (var db = new AgentStateDatabase(Path.Combine(dir, "s.db"), NullLogger<AgentStateDatabase>.Instance, hardenDirectoryAcl: false))
        {
            var inbox = new CommandInboxStore(db);
            var handler = new Handler();
            var outgoing = new OutgoingMessageChannel();
            var dispatcher = new TypedCommandDispatcher([handler], inbox, outgoing, TimeProvider.System, NullLogger<TypedCommandDispatcher>.Instance,
                new CommandSignatureVerifier(PublicKey, false, TimeProvider.System));

            await dispatcher.DispatchAsync("""{"type":"COMMAND","commandId":"forged","commandType":"backup.cancel","parameters":{"jobId":"j"}}""", BackupCommandTypes.Cancel);
            await dispatcher.DispatchAsync(SignedCommand("genuine", BackupCommandTypes.Cancel, """{"jobId":"j"}""", DateTimeOffset.UtcNow), BackupCommandTypes.Cancel);

            Assert.Equal(1, handler.Calls);
            // The forged id was never recorded, so it cannot block a later genuine command with that id.
            Assert.Equal(InboxDisposition.New, inbox.Begin("forged", "backup.cancel", null, null, DateTimeOffset.UtcNow).Disposition);
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(dir, true);
    }

    private sealed class Handler : ITypedCommandHandler
    {
        public int Calls { get; private set; }

        public string CommandType => BackupCommandTypes.Cancel;

        public Task<TypedCommandAcceptance> HandleAsync(TypedCommandContext context, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new TypedCommandAcceptance { Accepted = true });
        }
    }
}
