using CloudOrc.Agent.Contracts.Enrollment;
using CloudOrc.ControlAgent.Configuration;
using CloudOrc.ControlAgent.Enrollment;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudOrc.ControlAgent.Tests.Enrollment;

/// <summary>
/// Real filesystem + real Windows DPAPI round trips (no mocking) - this class exists
/// specifically to prove the encrypted-at-rest persistence actually works on this OS, not
/// just that the code compiles.
/// </summary>
public class EnrolledStateStoreTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "CloudOrcAgentTests-" + Guid.NewGuid().ToString("N"));

    private EnrolledStateStore CreateStore() =>
        new(Options.Create(new ControlAgentOptions { DataDirectory = _tempDir }), NullLogger<EnrolledStateStore>.Instance);

    private static EnrolledAgentState SampleState() => new()
    {
        AgentId = "agent-1",
        ServerId = "server-1",
        BackendUrl = "wss://backend.example.test/agent",
        Credential = "super-secret-credential",
        EnrolledAtUtc = DateTimeOffset.UtcNow
    };

    [Fact]
    public void TryLoad_NoFileExists_ReturnsNull()
    {
        var store = CreateStore();

        Assert.False(store.Exists());
        Assert.Null(store.TryLoad());
    }

    [Fact]
    public void SaveThenTryLoad_RoundTripsAllFields()
    {
        var store = CreateStore();
        var original = SampleState();

        store.Save(original);
        var loaded = store.TryLoad();

        Assert.True(store.Exists());
        Assert.NotNull(loaded);
        Assert.Equal(original.AgentId, loaded!.AgentId);
        Assert.Equal(original.ServerId, loaded.ServerId);
        Assert.Equal(original.BackendUrl, loaded.BackendUrl);
        Assert.Equal(original.Credential, loaded.Credential);
    }

    [Fact]
    public void Save_WritesCiphertextNotPlaintext()
    {
        var store = CreateStore();
        var state = SampleState();

        store.Save(state);

        var rawBytes = File.ReadAllBytes(Path.Combine(_tempDir, "enrollment.dat"));
        var rawText = System.Text.Encoding.UTF8.GetString(rawBytes);

        Assert.DoesNotContain(state.Credential, rawText);
        Assert.DoesNotContain(state.AgentId, rawText);
    }

    [Fact]
    public void TryLoad_TamperedFile_ReturnsNullInsteadOfThrowing()
    {
        var store = CreateStore();
        store.Save(SampleState());

        var path = Path.Combine(_tempDir, "enrollment.dat");
        var bytes = File.ReadAllBytes(path);
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] ^= 0xFF;
        }
        File.WriteAllBytes(path, bytes);

        var result = store.TryLoad();

        Assert.Null(result);
    }

    [Fact]
    public void SaveTwice_SecondSaveOverwritesTheFirst()
    {
        var store = CreateStore();
        store.Save(SampleState());
        store.Save(new EnrolledAgentState
        {
            AgentId = "agent-2",
            ServerId = "server-2",
            BackendUrl = "wss://other.example.test/agent",
            Credential = "different-credential",
            EnrolledAtUtc = DateTimeOffset.UtcNow
        });

        var loaded = store.TryLoad();

        Assert.Equal("agent-2", loaded!.AgentId);
    }

    [Fact]
    public void Delete_RemovesTheFile()
    {
        var store = CreateStore();
        store.Save(SampleState());

        store.Delete();

        Assert.False(store.Exists());
        Assert.Null(store.TryLoad());
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
