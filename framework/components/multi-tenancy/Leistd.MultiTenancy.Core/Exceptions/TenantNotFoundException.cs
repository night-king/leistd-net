using Leistd.ExceptionHandling;
using Leistd.MultiTenancy.Errors;

namespace Leistd.MultiTenancy.Exceptions;

/// <summary>表示租户不存在。</summary>
public class TenantNotFoundException : BusinessException
{
    /// <summary>构造异常。</summary>
    /// <param name="tenantIdOrName">解析出的租户 Id 或名称。</param>
    public TenantNotFoundException(string tenantIdOrName)
        : base(MultiTenancyErrorCodes.NotFound, $"Tenant not found: {tenantIdOrName}")
    {
        TenantIdOrName = tenantIdOrName;
    }

    /// <summary>解析出的租户 Id 或名称。</summary>
    public string TenantIdOrName { get; }
}
