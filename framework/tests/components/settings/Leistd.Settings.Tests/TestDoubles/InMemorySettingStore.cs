using Leistd.Settings.Definitions;
using Leistd.Settings.Stores;

namespace Leistd.Settings.Tests.TestDoubles;

// 真正保存写入的内存存储：读取返回副本（与数据库查询一样，之后的写入不会改动已读出的结果），
// 并记下每次写入，供写入入口的顺序与新鲜读取用例使用。线程安全，两个写入方可以并发调用。
internal sealed class InMemorySettingStore(Action<string>? journal = null) : ISettingStore
{
    private readonly Dictionary<(SettingScopes Scope, string? UserId), Dictionary<string, string>> _rows = [];

    public bool CanAccessHostScope => true;

    /// <summary>不为 <see langword="null"/> 时，下一次写入抛出它（写入不生效）。</summary>
    public Exception? NextWriteFailure { get; set; }

    public Task<IReadOnlyDictionary<string, string>> GetAllAsync(
        SettingScopes scope,
        string? userId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_rows)
        {
            IReadOnlyDictionary<string, string> copy = _rows.TryGetValue(Key(scope, userId), out var values)
                ? new Dictionary<string, string>(values, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);
            return Task.FromResult(copy);
        }
    }

    public Task RemoveAllAsync(CancellationToken cancellationToken = default)
    {
        lock (_rows)
        {
            _rows.Clear();
        }

        return Task.CompletedTask;
    }

    public Task SetAsync(
        string name,
        string? value,
        SettingScopes scope,
        string? userId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (NextWriteFailure is { } failure)
        {
            NextWriteFailure = null;
            return Task.FromException(failure);
        }

        var key = Key(scope, userId);
        lock (_rows)
        {
            if (!_rows.TryGetValue(key, out var values))
            {
                _rows[key] = values = new Dictionary<string, string>(StringComparer.Ordinal);
            }

            if (value is null)
            {
                values.Remove(name);
            }
            else
            {
                values[name] = value;
            }
        }

        journal?.Invoke($"store:{name}={value ?? "<cleared>"}");
        return Task.CompletedTask;
    }

    /// <summary>当前存储值；没有时为 <see langword="null"/>。</summary>
    public string? Get(string name, SettingScopes scope = SettingScopes.Tenant, string? userId = null)
    {
        var key = Key(scope, userId);
        lock (_rows)
        {
            return _rows.TryGetValue(key, out var values) && values.TryGetValue(name, out var value) ? value : null;
        }
    }

    // 宿主层与宿主的租户层同一行，与 EfCoreSettingStore 一致
    private static (SettingScopes Scope, string? UserId) Key(SettingScopes scope, string? userId)
        => (scope == SettingScopes.Host ? SettingScopes.Tenant : scope, userId);
}
