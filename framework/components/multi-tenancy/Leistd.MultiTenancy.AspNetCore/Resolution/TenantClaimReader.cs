using System.Security.Claims;
using Leistd.MultiTenancy.Exceptions;
using Leistd.Security.Claims;

namespace Leistd.MultiTenancy.AspNetCore.Resolution;

// claim → 租户：HTTP 解析链与 Hub 等非 HTTP 入口共用这一处，规则本身在 ClaimTypeOptions。
internal static class TenantClaimReader
{
    // 返回 null 表示宿主（无租户声明）；多条或非 GUID 抛 InvalidTenantClaimException，失败关闭。
    internal static Guid? Read(ClaimsPrincipal principal, ClaimTypeOptions claimTypes)
    {
        var tenant = claimTypes.ReadTenant(principal);
        return tenant.IsValid ? tenant.TenantId : throw new InvalidTenantClaimException(claimTypes.TenantId);
    }
}
