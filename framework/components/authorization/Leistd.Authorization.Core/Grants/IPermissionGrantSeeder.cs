using Leistd.MultiTenancy.Tenancy;

namespace Leistd.Authorization.Grants;

/// <summary>初始化时的首次授予：主体从未写过授予时，把某一侧别上的全部可用权限授给它。</summary>
/// <remarks>
/// <para>“从未写过”按授权版本为 0 判断；写过一次之后再次调用不会补回已撤销或新增的权限。</para>
/// <para>并发的两次初始化只有一次写入，后到的一方视为已播种。</para>
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
}
