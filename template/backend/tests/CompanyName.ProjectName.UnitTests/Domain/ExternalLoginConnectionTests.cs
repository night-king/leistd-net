#if (ExternalLogin)
using CompanyName.ProjectName.Domain.Auth.Entities;
using CompanyName.ProjectName.Domain.Auth.ValueObjects;

namespace CompanyName.ProjectName.UnitTests.Domain;

public sealed class ExternalLoginConnectionTests
{
    private static readonly DateTime InitialSync = new(2026, 8, 31, 2, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Creation_records_the_callers_time()
    {
        var connection = CreateConnection(InitialSync);

        Assert.Equal(InitialSync, connection.Profile.SyncedAt);
    }

    [Fact]
    public void Profile_update_records_the_callers_time()
    {
        var connection = CreateConnection(InitialSync);
        var updatedAt = InitialSync.AddMinutes(1);

        connection.Sync(new ExternalProfile(updatedAt, "updated-user", "updated@example.com", "https://example.com/avatar.png"));

        Assert.Equal(updatedAt, connection.Profile.SyncedAt);
        Assert.Equal("updated-user", connection.Profile.AccountLabel);
    }

    private static ExternalLoginConnection CreateConnection(DateTime syncedAt) =>
        new(
            Guid.Parse("01991a40-8a00-7000-8000-000000000001"),
            "github",
            "provider-user-id",
            new ExternalProfile(syncedAt, "provider-user", "provider@example.com", null));
}
#endif
