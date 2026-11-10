using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.BackupWorker.Engines;
using CloudOrc.BackupWorker.Storage;

namespace CloudOrc.BackupWorker.Tests;

public sealed class StorageTargetTests : IAsyncDisposable
{
    private readonly string _work = Directory.CreateTempSubdirectory("cloudorc-work-").FullName;
    private readonly GoogleDriveTokenBroker _broker = new(_ => Task.FromResult<GoogleDriveGrant?>(null));

    public async ValueTask DisposeAsync()
    {
        await _broker.DisposeAsync();
        Directory.Delete(_work, true);
    }

    private static StorageGrant DriveGrant() => new()
    {
        GrantId = "g1",
        Provider = StorageProviderTypes.GoogleDrive,
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        Repository = new GrantRepository { RepositoryId = "repo", Engine = BackupEngineNames.Kopia, Path = "cloudorc/tenant-a/repo", Password = "pw-not-in-args" },
        GoogleDrive = new GoogleDriveGrant { AccessToken = "ya29.secret-token", TokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1), RootFolderId = "folder-1" }
    };

    [Fact]
    public void GoogleDrive_TokenGoesOnlyIntoEnvironment_RefreshTokenIsADummy()
    {
        var storage = StorageTarget.Build(DriveGrant(), _work, new EngineBinary("rclone", "1", @"C:\Engines\rclone.exe"), _broker);

        var args = string.Join(' ', storage.KopiaStorageArgs.Concat(storage.ResticOptions).Append(storage.ResticRepository));
        Assert.DoesNotContain("ya29", args);
        Assert.DoesNotContain(_broker.ClientSecret, args);
        Assert.Contains("--remote-path=codrive:cloudorc/tenant-a/repo", storage.KopiaStorageArgs);
        Assert.Equal("rclone:codrive:cloudorc/tenant-a/repo", storage.ResticRepository);

        var token = JsonDocument.Parse(storage.Environment["RCLONE_CONFIG_CODRIVE_TOKEN"]!).RootElement;
        Assert.Equal("ya29.secret-token", token.GetProperty("access_token").GetString());
        Assert.Equal(GoogleDriveTokenBroker.DummyRefreshToken, token.GetProperty("refresh_token").GetString());
        Assert.Equal(_broker.TokenUrl, storage.Environment["RCLONE_CONFIG_CODRIVE_TOKEN_URL"]);
        Assert.Equal("drive.file", storage.Environment["RCLONE_CONFIG_CODRIVE_SCOPE"]);
        Assert.Equal("folder-1", storage.Environment["RCLONE_CONFIG_CODRIVE_ROOT_FOLDER_ID"]);
        Assert.StartsWith("http://127.0.0.1:", _broker.TokenUrl);
    }

    [Fact]
    public void S3_CredentialsGoOnlyIntoEnvironment()
    {
        var grant = new StorageGrant
        {
            GrantId = "g2",
            Provider = StorageProviderTypes.S3,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            Repository = new GrantRepository { RepositoryId = "repo", Engine = BackupEngineNames.Restic, Path = "repo-1", Password = "pw" },
            S3 = new S3Grant { Endpoint = "https://s3.wasabisys.example", Region = "ap-southeast-1", Bucket = "cloudorc-dr", Prefix = "tenants/t1", AccessKeyId = "AKIAEXAMPLEKEY000000", SecretAccessKey = "very-secret", SessionToken = "sess" }
        };

        var storage = StorageTarget.Build(grant, _work, null, null);
        var args = string.Join(' ', storage.KopiaStorageArgs.Append(storage.ResticRepository));

        Assert.DoesNotContain("very-secret", args);
        Assert.DoesNotContain("AKIAEXAMPLEKEY000000", args);
        Assert.Equal("s3:https://s3.wasabisys.example/cloudorc-dr/tenants/t1/repo-1", storage.ResticRepository);
        Assert.Contains("--prefix=tenants/t1/repo-1/", storage.KopiaStorageArgs);
        Assert.Equal("very-secret", storage.Environment["AWS_SECRET_ACCESS_KEY"]);
    }

    [Theory]
    [InlineData("a/../b")]
    [InlineData("./x")]
    public void RepositoryPath_WithRelativeSegments_IsRejected(string path) =>
        Assert.Throws<InvalidOperationException>(() => StorageTarget.NormalizeRepoPath(path));
}

public sealed class GoogleDriveTokenBrokerTests
{
    [Fact]
    public async Task ReturnsCurrentToken_OnlyToCallersPresentingTheJobSecret()
    {
        var grant = new GoogleDriveGrant { AccessToken = "ya29.fresh", TokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(50) };
        await using var broker = new GoogleDriveTokenBroker(_ => Task.FromResult<GoogleDriveGrant?>(grant));
        using var http = new HttpClient();

        var denied = await http.PostAsync(broker.TokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token", ["refresh_token"] = GoogleDriveTokenBroker.DummyRefreshToken, ["client_id"] = GoogleDriveTokenBroker.ClientId, ["client_secret"] = "guess"
        }));
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, denied.StatusCode);

        // Basic-auth style (what Go's oauth2 tries first).
        using var request = new HttpRequestMessage(HttpMethod.Post, broker.TokenUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = GoogleDriveTokenBroker.DummyRefreshToken })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{GoogleDriveTokenBroker.ClientId}:{broker.ClientSecret}")));
        var ok = await http.SendAsync(request);
        Assert.Equal(System.Net.HttpStatusCode.OK, ok.StatusCode);

        var body = JsonDocument.Parse(await ok.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("ya29.fresh", body.GetProperty("access_token").GetString());
        Assert.InRange(body.GetProperty("expires_in").GetInt32(), 2900, 3000);
    }

    [Fact]
    public async Task RenewalFailure_IsReportedAsInvalidGrant()
    {
        await using var broker = new GoogleDriveTokenBroker(_ => Task.FromResult<GoogleDriveGrant?>(null));
        using var http = new HttpClient();
        var response = await http.PostAsync(broker.TokenUrl, new FormUrlEncodedContent(new Dictionary<string, string> { ["client_secret"] = broker.ClientSecret }));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_grant", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task WrongPath_IsNotFound()
    {
        await using var broker = new GoogleDriveTokenBroker(_ => Task.FromResult<GoogleDriveGrant?>(null));
        using var http = new HttpClient();
        var wrong = broker.TokenUrl.Replace("/token", "/other", StringComparison.Ordinal);
        var response = await http.PostAsync(wrong, new StringContent(""));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }
}

public class WorkerChannelTests
{
    private static StorageGrant Grant(string id) => new()
    {
        GrantId = id,
        Provider = StorageProviderTypes.S3,
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        Repository = new GrantRepository { RepositoryId = "r", Engine = "kopia", Path = "p", Password = "x" }
    };

    [Fact]
    public async Task GrantRenewal_RoundTrip_AndCancel()
    {
        var input = new BlockingTextReader();
        var output = new StringWriter();
        var channel = new WorkerChannel(input, output, Grant("g1"));
        var listen = Task.Run(channel.ListenAsync);

        var renewal = channel.RenewGrantAsync(CancellationToken.None);
        await Task.Delay(50);
        Assert.Contains("\"type\":\"grant.renew\"", output.ToString());

        input.Push(JsonSerializer.Serialize(new WorkerEnvelope { Type = WorkerMessageTypes.GrantRenewed, Grant = Grant("g2") }, WorkerEnvelope.JsonOptions));
        var renewed = await renewal;
        Assert.Equal("g2", renewed!.GrantId);
        Assert.Equal("g2", channel.CurrentGrant.GrantId);

        input.Push(JsonSerializer.Serialize(new WorkerEnvelope { Type = WorkerMessageTypes.Cancel }, WorkerEnvelope.JsonOptions));
        await Task.Delay(50);
        Assert.True(channel.Cancellation.IsCancellationRequested);

        input.Complete();
        await listen;
    }

    [Fact]
    public async Task SupervisorClosingStdin_CancelsTheJob()
    {
        var input = new BlockingTextReader();
        var channel = new WorkerChannel(input, new StringWriter(), Grant("g1"));
        var listen = Task.Run(channel.ListenAsync);
        input.Complete();
        await listen;
        Assert.True(channel.Cancellation.IsCancellationRequested);
    }

    [Fact]
    public void LogAndResultMessages_AreRedacted()
    {
        var output = new StringWriter();
        var channel = new WorkerChannel(new StringReader(""), output, Grant("g1"));
        channel.Log("info", "token=abcdef123456 leaked");
        channel.Result(new WorkerResult { State = BackupJobStates.Failed, Message = "Bearer abc.def" });
        Assert.DoesNotContain("abcdef123456", output.ToString());
        Assert.DoesNotContain("abc.def", output.ToString());
    }

    private sealed class BlockingTextReader : TextReader
    {
        private readonly System.Collections.Concurrent.BlockingCollection<string?> _lines = new();

        public void Push(string line) => _lines.Add(line);

        public void Complete() => _lines.Add(null);

        public override Task<string?> ReadLineAsync() => Task.Run(() => _lines.Take());

        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) => new(Task.Run(() => _lines.Take(cancellationToken), cancellationToken));
    }
}
