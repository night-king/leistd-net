using Leistd.ExceptionHandling;
using Leistd.MultiTenancy.Errors;

namespace Leistd.MultiTenancy.Exceptions;

/// <summary>表示尝试在启用中的租户上做一次会改变数据落点的连接变更。</summary>
/// <remarks>
/// <para>两类写入要求先停用：改或删已有的一行（路由换库，否则新旧实例同时写入不同物理库），
/// 以及给无登记的租户登记第一条（变为分库，既有数据与租户管理员不会随之迁移）。</para>
/// <para>已是分库租户补登此前缺失的名字不在此列：该服务此前失败关闭，没有数据搁浅，启用态下照常放行。</para>
/// </remarks>
public class TenantConnectionChangeRequiresInactiveTenantException : BusinessException
{
    /// <summary>构造异常。</summary>
    /// <param name="tenantId">目标租户。</param>
    public TenantConnectionChangeRequiresInactiveTenantException(Guid tenantId)
        : base(MultiTenancyErrorCodes.ConnectionChangeRequiresInactiveTenant,
            $"Deactivate tenant '{tenantId}' before changing its database connection.")
    {
        TenantId = tenantId;
    }

    /// <summary>目标租户标识。</summary>
    public Guid TenantId { get; }
}
