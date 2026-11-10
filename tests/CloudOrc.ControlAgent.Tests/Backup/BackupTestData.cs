using CloudOrc.Agent.Contracts.Backup;

namespace CloudOrc.ControlAgent.Tests.Backup;

internal static class BackupTestData
{
    public static BackupPolicyDocument Policy(
        string id = "pol-1",
        string version = "ver-1",
        BackupSchedule? schedule = null,
        IReadOnlyList<BackupSource>? sources = null,
        string engine = BackupEngineNames.Kopia) => new()
    {
        PolicyId = id,
        PolicyVersionId = version,
        EffectiveHash = "hash-" + version,
        Sources = sources ?? [new BackupSource { Path = @"D:\Tally\Data", Kind = BackupSourceKinds.Tally }],
        Schedule = schedule ?? new BackupSchedule { Kind = BackupScheduleKinds.Daily, TimesOfDay = ["01:00"], WindowMinutes = 120, TimeZoneId = "UTC" },
        Repository = new BackupRepositoryRef { RepositoryId = "repo-1", StoragePoolId = "pool-gdrive", Engine = engine }
    };

    public static StorageGrant Grant(string repositoryId = "repo-1", string engine = BackupEngineNames.Kopia, DateTimeOffset? expires = null) => new()
    {
        GrantId = "grant-" + Guid.NewGuid().ToString("N")[..6],
        Provider = StorageProviderTypes.GoogleDrive,
        ExpiresAt = expires ?? DateTimeOffset.UtcNow.AddHours(1),
        Repository = new GrantRepository { RepositoryId = repositoryId, Engine = engine, Path = "cloudorc/repo-1", Password = "unit-test-only" },
        GoogleDrive = new GoogleDriveGrant { AccessToken = "ya29.unit-test", TokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1), RootFolderId = "folder" }
    };
}
