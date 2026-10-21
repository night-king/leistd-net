using Leistd.Disposables;
using Microsoft.Extensions.Logging;

namespace Leistd.MultiTenancy.Context;

/// <summary>通过 <see cref="ICurrentTenantAccessor"/> 提供当前租户上下文。</summary>
/// <remarks>
/// 切换租户的同时打开日志作用域（<see cref="TenantLogKeys.TenantId"/>）：所有切换入口的日志都带上租户，
/// 切回宿主时写入 <see langword="null"/>，覆盖外层租户。
/// </remarks>
/// <param name="accessor">租户上下文存取器。</param>
/// <param name="logger">用于打开日志作用域；为 <see langword="null"/> 时不写。</param>
public class CurrentTenant(ICurrentTenantAccessor accessor, ILogger<CurrentTenant>? logger = null) : ICurrentTenant
{
    /// <inheritdoc />
    public bool IsAvailable => Id.HasValue;

    /// <inheritdoc />
    public Guid? Id => accessor.Current?.TenantId;

    /// <inheritdoc />
    public string? Name => accessor.Current?.Name;

    /// <inheritdoc />
    public IDisposable Change(Guid? id, string? name = null)
    {
        var parent = accessor.Current;
        accessor.Current = new BasicTenantInfo(id, name);
        var logScope = logger?.BeginScope(new Dictionary<string, object?> { [TenantLogKeys.TenantId] = id });

        return new DisposeAction(() =>
        {
            logScope?.Dispose();
            accessor.Current = parent;
        });
    }
}
