#if (RemoteTokenAuth)
using System.Collections.Concurrent;
using CompanyName.ProjectName.Domain.Users.Abstractions;

namespace CompanyName.ProjectName.IntegrationTests.Fixtures;

/// <summary>测试宿主里代替 Identity 的账户偏好端口：只存个人值，生效值等于个人值（组织层与部署基线不在资源服务的测试范围）。</summary>
internal sealed class InMemoryAccountPreferences : IIdentityAccountPreferences
{
    public ConcurrentDictionary<Guid, AccountPreferenceValues> Values { get; } = new();

    public Task<AccountPreferenceSnapshot?> ReadAsync(
        Guid? tenantId,
        Guid userId,
        AccountPreferenceReadMode mode,
        CancellationToken cancellationToken = default)
        => Task.FromResult<AccountPreferenceSnapshot?>(Snapshot(userId));

    public Task<AccountPreferenceSnapshot> WriteForCurrentUserAsync(
        Guid? tenantId,
        Guid userId,
        string field,
        string? value,
        CancellationToken cancellationToken = default)
    {
        Values.AddOrUpdate(
            userId,
            _ => Apply(new AccountPreferenceValues(), field, value),
            (_, current) => Apply(current, field, value));
        return Task.FromResult(Snapshot(userId));
    }

    private AccountPreferenceSnapshot Snapshot(Guid userId)
    {
        var values = Values.GetValueOrDefault(userId) ?? new AccountPreferenceValues();
        return new AccountPreferenceSnapshot { Preferences = values, PreferenceOverrides = values };
    }

    private static AccountPreferenceValues Apply(AccountPreferenceValues current, string field, string? value) => field switch
    {
        "timeZone" => current with { TimeZone = value },
        "theme" => current with { Theme = value },
        _ => current with { Language = value },
    };
}
#endif
