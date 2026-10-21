using System.Security.Claims;
using Leistd.Authorization.Definitions;
using Leistd.Authorization.Grants;
using Leistd.Authorization.Subjects;
using Leistd.MultiTenancy.Context;
using Leistd.MultiTenancy.Tenancy;
using Leistd.Security.Claims;
using Microsoft.Extensions.Options;

namespace Leistd.Authorization.Checking;

/// <summary>默认权限检查器。</summary>
/// <remarks>
/// <para>以 Scoped 注册：当前主体的解析与授予读取在作用域内缓存，其后的检查都是内存查找；
/// 当前主体或租户在作用域内被切换时按新的主体与租户重新加载。
/// 显式传入的其他主体每次单独解析、不进缓存，避免同一作用域里先后判两个主体时串用授予。</para>
/// <para>完整判定顺序见 authorization 组件文档。</para>
/// </remarks>
public class DefaultPermissionChecker(
    IPermissionSubjectProvider subjectProvider,
    IPermissionDefinitionManager permissionDefinitionManager,
    IPermissionGrantStore permissionGrantStore,
    ICurrentTenant? currentTenant = null,
    ICurrentPrincipalAccessor? principalAccessor = null,
    IOptions<ClaimTypeOptions>? claimTypes = null) : IPermissionChecker, IDisposable
{
    private readonly ClaimTypeOptions _claimTypes = claimTypes?.Value ?? new ClaimTypeOptions();

    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private SubjectGrants? _current;

    private MultiTenancySides CurrentSide =>
        currentTenant?.IsAvailable == true ? MultiTenancySides.Tenant : MultiTenancySides.Host;

    // 未启用、或侧别不符：先于授予与超管旁路——宿主侧权限在租户上下文内对任何主体都不可用
    private bool IsCheckable(string name) =>
        permissionDefinitionManager.IsEffectivelyEnabled(name) &&
        permissionDefinitionManager.IsAvailableOn(name, CurrentSide);

    /// <inheritdoc />
    public async Task<bool> IsGrantedAsync(string name, CancellationToken cancellationToken = default)
        => IsCheckable(name) && (await LoadCurrentAsync(cancellationToken)).Has(name);

    /// <inheritdoc />
    public Task<MultiplePermissionGrantResult> IsGrantedAsync(
        string[] names,
        CancellationToken cancellationToken = default)
        => EvaluateAsync(names, LoadCurrentAsync, cancellationToken);

    /// <inheritdoc />
    public async Task<bool> IsGrantedAsync(
        ClaimsPrincipal principal,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return IsCheckable(name) && (await LoadAsync(principal, cancellationToken)).Has(name);
    }

    /// <inheritdoc />
    public Task<MultiplePermissionGrantResult> IsGrantedAsync(
        ClaimsPrincipal principal,
        string[] names,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return EvaluateAsync(names, ct => LoadAsync(principal, ct), cancellationToken);
    }

    /// <summary>释放加载闸门。</summary>
    public void Dispose() => _loadLock.Dispose();

    private async Task<MultiplePermissionGrantResult> EvaluateAsync(
        string[] names,
        Func<CancellationToken, Task<SubjectGrants>> load,
        CancellationToken cancellationToken)
    {
        if (names == null || names.Length == 0)
            return new MultiplePermissionGrantResult(new Dictionary<string, bool>());

        var results = names
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(x => x, _ => false, StringComparer.Ordinal);

        if (results.Count == 0)
            return new MultiplePermissionGrantResult(results);

        var grants = await load(cancellationToken);

        foreach (var name in results.Keys.ToArray())
        {
            results[name] = IsCheckable(name) && grants.Has(name);
        }

        return new MultiplePermissionGrantResult(results);
    }

    // 就是当前主体（同一引用）时走作用域缓存；授权处理器评估的 context.User 通常即是它
    private Task<SubjectGrants> LoadAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
        => principalAccessor?.Principal is { } current && ReferenceEquals(current, principal)
            ? LoadCurrentAsync(cancellationToken)
            : LoadExplicitAsync(principal, cancellationToken);

    // 快照记下为哪个主体、哪个租户加载：作用域内当前主体或租户被切换时重新加载，不串用前一份授予
    private async Task<SubjectGrants> LoadCurrentAsync(CancellationToken cancellationToken)
    {
        var principal = principalAccessor?.Principal;
        var tenantId = currentTenant?.Id;
        if (_current is { } loaded && loaded.IsFor(principal, tenantId))
            return loaded;

        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            if (_current is { } reloaded && reloaded.IsFor(principal, tenantId))
                return reloaded;

            // 主体的租户声明非法时失败关闭。只校验合法性，不要求等于当前租户：宿主主体可显式切入租户
            var grants = principal is not null && !_claimTypes.ReadTenant(principal).IsValid
                ? SubjectGrants.None
                : await ReadGrantsAsync(
                    await subjectProvider.GetCurrentSubjectAsync(cancellationToken),
                    cancellationToken);
            return _current = grants with { LoadedFor = principal, TenantId = tenantId };
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private async Task<SubjectGrants> LoadExplicitAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        // 授予按当前租户读取：别的租户（或宿主）的主体在这里没有可判的授予
        if (!BelongsToCurrentTenant(principal))
            return SubjectGrants.None;

        return await ReadGrantsAsync(
            await subjectProvider.GetSubjectAsync(principal, cancellationToken),
            cancellationToken);
    }

    // 租户声明按 ClaimTypeOptions 读取（与租户解析同一规则），非法声明失败关闭；未接多租户时只校验合法性
    private bool BelongsToCurrentTenant(ClaimsPrincipal principal)
    {
        var tenant = _claimTypes.ReadTenant(principal);
        return tenant.IsValid && (currentTenant is null || tenant.TenantId == currentTenant.Id);
    }

    // 超级管理员旁路功能权限，无需读取授予记录
    private async Task<SubjectGrants> ReadGrantsAsync(PermissionSubject? subject, CancellationToken cancellationToken)
    {
        if (subject is not { IsSuperAdmin: false })
            return new SubjectGrants(subject, null);

        var grants = await permissionGrantStore.GetGrantsForSubjectAsync(
            subject.UserId,
            subject.RoleIds,
            cancellationToken);

        return new SubjectGrants(subject, grants.GetGrantedNames());
    }

    private sealed record SubjectGrants(PermissionSubject? Subject, IReadOnlySet<string>? Granted)
    {
        public static SubjectGrants None { get; } = new(null, null);

        public ClaimsPrincipal? LoadedFor { get; init; }

        public Guid? TenantId { get; init; }

        public bool IsFor(ClaimsPrincipal? principal, Guid? tenantId) =>
            ReferenceEquals(LoadedFor, principal) && TenantId == tenantId;

        public bool Has(string name) =>
            Subject is { } subject && (subject.IsSuperAdmin || Granted?.Contains(name) == true);
    }
}
