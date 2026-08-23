using CloudOrc.AgentTestServer.Enrollment;

namespace CloudOrc.AgentTestServer.Tests;

public class CredentialStoreTests
{
    [Fact]
    public void IssueCredential_ThenIsValid_ReturnsTrue()
    {
        var store = new CredentialStore();

        var credential = store.IssueCredential("agent-1");

        Assert.True(store.IsValid(credential));
    }

    [Fact]
    public void IsValid_NeverIssuedCredential_ReturnsFalse()
    {
        var store = new CredentialStore();

        Assert.False(store.IsValid("never-issued"));
    }

    [Fact]
    public void Revoke_ThenIsValid_ReturnsFalse_RevokedAgentCannotAuthenticate()
    {
        var store = new CredentialStore();
        var credential = store.IssueCredential("agent-1");

        var revoked = store.Revoke(credential);

        Assert.True(revoked);
        Assert.False(store.IsValid(credential));
    }

    [Fact]
    public void Revoke_UnknownCredential_ReturnsFalse()
    {
        var store = new CredentialStore();

        Assert.False(store.Revoke("never-issued"));
    }

    [Fact]
    public void IssuedCredentials_AreUniquePerAgent()
    {
        var store = new CredentialStore();

        var credentialA = store.IssueCredential("agent-1");
        var credentialB = store.IssueCredential("agent-2");

        Assert.NotEqual(credentialA, credentialB);
    }
}
