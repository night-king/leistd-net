using Leistd.ExceptionHandling;
using Leistd.MultiTenancy.Errors;

namespace Leistd.MultiTenancy.Exceptions;

/// <summary>表示租户已停用。</summary>
public class TenantNotActiveException : BusinessException
{
    /// <summary>构造异常。</summary>
    /// <param name="tenantIdOrName">解析出的租户 Id 或名称。</param>
    public TenantNotActiveException(string tenantIdOrName)
        : base(MultiTenancyErrorCodes.NotActive, $"Tenant is not active: {tenantIdOrName}")
    {
        TenantIdOrName = tenantIdOrName;
    }

    /// <summary>解析出的租户 Id 或名称。</summary>
    public string TenantIdOrName { get; }
}
