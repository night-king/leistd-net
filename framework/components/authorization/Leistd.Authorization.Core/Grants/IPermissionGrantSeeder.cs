using Leistd.MultiTenancy.Tenancy;

namespace Leistd.Authorization.Grants;

/// <summary>给内置管理员这类角色授予某一侧别上的全部可用权限：初始化时首次授予，升级后补缺。</summary>
/// <remarks>
/// <para><see cref="SeedAllAsync"/> 只在主体从未写过授予时写入（授权版本为 0），写过之后不再补回已撤销或新增的权限；
/// 并发的两次初始化只有一次写入，后到的一方视为已播种。</para>
/// <para><see cref="TopUpAsync"/> 只补缺口，已撤销的权限也会补回；已有授予（包括定义已删除的旧名）不动。</para>
/// </remarks>
public interface IPermissionGrantSeeder
{
    /// <summary>主体从未写过授予时授予全部可用权限。</summary>
    /// <param name="providerName">授予对象类型。</param>
    /// <param name="providerKey">授予对象 Key。</param>
    /// <param name="side">只授予该侧别上可用的权限。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>写入的权限数；主体已有授予历史时为 <see langword="null"/>。</returns>
    Task<int?> SeedAllAsync(
        string providerName,
        string providerKey,
        MultiTenancySides side,
        CancellationToken cancellationToken = default);

    /// <summary>列出某一侧别上可用、主体却还没有的权限，不写入。</summary>
    /// <param name="providerName">授予对象类型。</param>
    /// <param name="providerKey">授予对象 Key。</param>
    /// <param name="side">只考虑该侧别上可用的权限。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>缺少的权限名，按定义顺序。</returns>
    Task<IReadOnlyList<string>> GetMissingAsync(
        string providerName,
        string providerKey,
        MultiTenancySides side,
        CancellationToken cancellationToken = default);

    /// <summary>把某一侧别上可用、主体却还没有的权限补授给它；没有缺口时不写入。</summary>
    /// <remarks>
    /// 逐项经 <see cref="IPermissionGrantManager.GrantAsync"/> 写入，与并发的在线编辑按版本冲突重试；中途失败时已补的保留，再次调用补齐其余。
    /// </remarks>
    /// <param name="providerName">授予对象类型。</param>
    /// <param name="providerKey">授予对象 Key。</param>
    /// <param name="side">只补该侧别上可用的权限。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>补授的权限名；没有缺口时为空。</returns>
    Task<IReadOnlyList<string>> TopUpAsync(
        string providerName,
        string providerKey,
        MultiTenancySides side,
        CancellationToken cancellationToken = default);
}
