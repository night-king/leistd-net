using Leistd.Settings.Definitions;
using Leistd.Settings.Stores;
using Leistd.Settings.Exceptions;

namespace Leistd.Settings.Tests.TestDoubles;

// 记录读取次数的内存存储：回落顺序与"请求内只查一次"都要靠它观察。
internal sealed class FakeSettingStore : ISettingStore
{
    public Dictionary<string, string> Tenant { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> User { get; } = new(StringComparer.Ordinal);

    /// <summary>宿主层（进程级）那一行。</summary>
    public Dictionary<string, string> Host { get; } = new(StringComparer.Ordinal);

    /// <summary>当前上下文能否读写宿主层；置 false 即模拟租户上下文。</summary>
    public bool CanAccessHostScope { get; set; } = true;

    public int HostReads { get; private set; }

    public int TenantReads { get; private set; }

    public int UserReads { get; private set; }

    /// <summary>大于 0 时，接下来这么多次用户级读取抛异常；用于模拟查询失败后重试。</summary>
    public int FailingUserReads { get; set; }

    public List<(string Name, string? Value, SettingScopes Scope, string? UserId)> Writes { get; } = [];

    public Task<IReadOnlyDictionary<string, string>> GetAllAsync(
        SettingScopes scope,
        string? userId,
        CancellationToken cancellationToken = default)
    {
        if (scope == SettingScopes.Host)
        {
            if (!CanAccessHostScope)
            {
                // 与 EfCoreSettingStore 一致：租户上下文下这一层根本不该被读到
                return Task.FromException<IReadOnlyDictionary<string, string>>(
                    new HostScopeUnavailableException());
            }

            HostReads++;
            return Task.FromResult<IReadOnlyDictionary<string, string>>(Host);
        }

        if (scope == SettingScopes.User)
        {
            UserReads++;
            if (FailingUserReads > 0)
            {
                FailingUserReads--;
                return Task.FromException<IReadOnlyDictionary<string, string>>(new InvalidOperationException("user read failed"));
            }

            return Task.FromResult<IReadOnlyDictionary<string, string>>(User);
        }

        TenantReads++;
        return Task.FromResult<IReadOnlyDictionary<string, string>>(Tenant);
    }

    public int RemoveAllCalls { get; private set; }

    public Task RemoveAllAsync(CancellationToken cancellationToken = default)
    {
        RemoveAllCalls++;
        Tenant.Clear();
        User.Clear();
        Host.Clear();
        return Task.CompletedTask;
    }

    public Task SetAsync(
        string name,
        string? value,
        SettingScopes scope,
        string? userId,
        CancellationToken cancellationToken = default)
    {
        Writes.Add((name, value, scope, userId));
        return Task.CompletedTask;
    }
}
