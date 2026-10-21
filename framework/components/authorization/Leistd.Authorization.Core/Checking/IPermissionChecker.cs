using System.Security.Claims;

namespace Leistd.Authorization.Checking;

/// <summary>检查权限授予状态。</summary>
/// <example>
/// <code>
/// if (!await permissionChecker.IsGrantedAsync("Orders.Update", ct))
///     throw new UnauthorizedAccessException();
///
/// // 批量检查：一次读取，其后都是内存查找
/// var result = await permissionChecker.IsGrantedAsync(["Orders.Read", "Orders.Update"], ct);
/// if (!result.AllGranted) { /* 入参为空时同样不通过 */ }
/// </code>
/// </example>
public interface IPermissionChecker
{
    /// <summary>检查当前用户是否拥有指定权限；未定义、未启用或当前侧别不可用的权限返回 <see langword="false"/>。</summary>
    Task<bool> IsGrantedAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>检查当前用户是否拥有指定的多个权限；一次读取授予，其后为内存查找。</summary>
    Task<MultiplePermissionGrantResult> IsGrantedAsync(
        string[] names,
        CancellationToken cancellationToken = default);

    /// <summary>检查指定认证主体是否拥有指定权限。</summary>
    /// <remarks>
    /// 主体就是当前主体时与 <see cref="IsGrantedAsync(string, CancellationToken)"/> 共用作用域内的缓存；
    /// 其他主体每次单独解析、不进缓存，且其租户声明与当前租户不一致时一律不授予。
    /// </remarks>
    Task<bool> IsGrantedAsync(ClaimsPrincipal principal, string name, CancellationToken cancellationToken = default);

    /// <summary>检查指定认证主体是否拥有指定的多个权限，规则同 <see cref="IsGrantedAsync(ClaimsPrincipal, string, CancellationToken)"/>。</summary>
    Task<MultiplePermissionGrantResult> IsGrantedAsync(
        ClaimsPrincipal principal,
        string[] names,
        CancellationToken cancellationToken = default);
}
/// <summary>多个权限的检查结果。</summary>
/// <param name="Results">权限名 → 是否授予。</param>
public readonly record struct MultiplePermissionGrantResult(
    IReadOnlyDictionary<string, bool> Results)
{
    /// <summary>是否所有权限都已授予；空集合返回 <see langword="false"/>（与 <see cref="Enumerable.All{TSource}"/> 相反）。</summary>
    public bool AllGranted => Results.Count > 0 && Results.Values.All(x => x);

    /// <summary>是否至少有一个权限已授予；空集合返回 <see langword="false"/>。</summary>
    public bool AnyGranted => Results.Values.Any(x => x);
}
