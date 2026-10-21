using Leistd.Authorization.Constants;
using Leistd.Authorization.Resource.Exceptions;

namespace Leistd.Authorization.Resource.Grants;

/// <summary>资源实例 ACL 的授予效果。</summary>
/// <remarks>与纯加法的功能权限不同，资源 ACL 支持显式拒绝（如共享给部门同时排除某人）。</remarks>
public enum ResourceGrantEffect
{
    /// <summary>显式允许。</summary>
    Granted = 1,

    /// <summary>显式拒绝。优先于任何来源的允许。</summary>
    Prohibited = 2
}

/// <summary>单条资源 ACL 授予。</summary>
/// <param name="Operation">被授予的操作。</param>
/// <param name="ProviderName">授予对象类型，见 <see cref="PermissionGrantProviderNames"/>。</param>
/// <param name="ProviderKey">授予对象 Key。</param>
/// <param name="Effect">授予效果。</param>
public sealed record ResourceGrant(
    string Operation,
    string ProviderName,
    string ProviderKey,
    ResourceGrantEffect Effect);

/// <summary>某个资源实例的完整 ACL 与其版本。</summary>
/// <param name="ResourceName">资源类型名。</param>
/// <param name="ResourceKey">资源实例 Key。</param>
/// <param name="Grants">该实例上的全部授予。</param>
/// <param name="Version">ACL 版本，尚未写入过时为 0。保存时回传即可获得乐观并发保护。</param>
public sealed record ResourceGrantSet(
    string ResourceName,
    string ResourceKey,
    IReadOnlyList<ResourceGrant> Grants,
    long Version);

/// <summary>资源 ACL 的只读存储。</summary>
/// <remarks>
/// 列表、统计与导出用集合级入口：返回可被数据库翻译的 <see cref="IQueryable{T}"/>，调用方以 <c>Contains</c> 合并进业务查询，
/// 不要先加载候选再逐条判定。
/// </remarks>
public interface IResourceGrantStore
{
    /// <summary>获取某个资源实例上的全部 ACL 授予与当前版本。</summary>
    /// <remarks>保存时把版本回传给 <see cref="IResourceGrantManager.ReplaceGrantsAsync"/> 以获得乐观并发保护。</remarks>
    Task<ResourceGrantSet> GetGrantsAsync(
        string resourceName,
        string resourceKey,
        CancellationToken cancellationToken = default);

    /// <summary>获取指定主体在某个资源实例上对每个操作的最终效果（拒绝优先）。</summary>
    Task<IReadOnlyDictionary<string, ResourceGrantEffect>> GetEffectiveGrantsAsync(
        string resourceName,
        string resourceKey,
        string userId,
        IReadOnlyCollection<string> roleIds,
        CancellationToken cancellationToken = default);

    /// <summary>构造“指定主体被授予某操作的资源 Key”查询，用于把 ACL 合并进集合查询；已排除显式拒绝。</summary>
    /// <remarks>
    /// 须与调用方的业务查询出自同一个 DbContext 实例，EF 才能把 <c>Contains</c> 翻译进同一条 SQL。
    /// 业务实体应持有与 ACL 一致的字符串资源 Key 列。
    /// </remarks>
    /// <example>
    /// <code>
    /// var keys = await store.QueryGrantedResourceKeysAsync("Orders", ResourceOperations.Read, userId, roleIds);
    /// query = query.Where(order =&gt; keys.Contains(order.ResourceKey));
    /// </code>
    /// </example>
    Task<IQueryable<string>> QueryGrantedResourceKeysAsync(
        string resourceName,
        string operation,
        string userId,
        IReadOnlyCollection<string> roleIds,
        CancellationToken cancellationToken = default);

    /// <summary>构造“指定主体被显式拒绝某操作的资源 Key”查询。</summary>
    /// <remarks>
    /// <see cref="QueryGrantedResourceKeysAsync"/> 减不掉经其他途径（如数据范围）可见的资源。
    /// 完整可见集合是 <c>(数据范围 OR ACL 允许) AND NOT ACL 拒绝</c>，因此还要减去本方法返回的拒绝集。
    /// 同样要求与业务查询出自同一个 DbContext 实例。
    /// </remarks>
    /// <example>
    /// <code>
    /// var denied = await store.QueryDeniedResourceKeysAsync("Orders", ResourceOperations.Read, userId, roleIds);
    /// query = query.Where(order =&gt; !denied.Contains(order.ResourceKey));
    /// </code>
    /// </example>
    Task<IQueryable<string>> QueryDeniedResourceKeysAsync(
        string resourceName,
        string operation,
        string userId,
        IReadOnlyCollection<string> roleIds,
        CancellationToken cancellationToken = default);
}

/// <summary>资源 ACL 的写入入口。</summary>
public interface IResourceGrantManager
{
    /// <summary>原子替换某个资源实例上的全部 ACL，返回写入后的版本。</summary>
    /// <param name="resourceName">资源类型名。</param>
    /// <param name="resourceKey">资源实例 Key。</param>
    /// <param name="grants">替换后的完整 ACL。</param>
    /// <param name="expectedVersion">
    /// 期望的当前版本，通常来自 <see cref="IResourceGrantStore.GetGrantsAsync"/>。
    /// 与存储中的版本不一致时抛 <see cref="ResourceGrantConcurrencyException"/>。
    /// <see langword="null"/> 表示不做并发校验，仅用于种子数据这类无并发编辑的场景。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<long> ReplaceGrantsAsync(
        string resourceName,
        string resourceKey,
        IReadOnlyCollection<ResourceGrant> grants,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default);

    /// <summary>清理某个资源实例的全部 ACL；幂等。资源被删除后调用。</summary>
    /// <remarks>ACL 表对业务表没有外键，还应安排周期性的孤儿记录清理。</remarks>
    Task<int> RemoveResourceAsync(
        string resourceName,
        string resourceKey,
        CancellationToken cancellationToken = default);

    /// <summary>清理某个主体在全部资源上的 ACL 条目；幂等。用户、角色或客户端被永久删除后必须调用。</summary>
    /// <remarks>
    /// ACL 表对身份表没有外键，不清理时主体标识被重用会让旧条目重新生效。应与
    /// <c>IPermissionGrantManager.RemoveProviderAsync</c> 在同一删除流程里成对调用；软删除不应调用。
    /// </remarks>
    Task<int> RemoveProviderAsync(
        string providerName,
        string providerKey,
        CancellationToken cancellationToken = default);
}
