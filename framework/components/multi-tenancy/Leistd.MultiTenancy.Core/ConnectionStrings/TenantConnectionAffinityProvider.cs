using Leistd.Data.Connections;
using Leistd.MultiTenancy.Context;

namespace Leistd.MultiTenancy.ConnectionStrings;

// 以当前租户作为工作单元的连接归属标识：共享库中也能阻止一个工作单元跨租户写入。
internal sealed class TenantConnectionAffinityProvider(ICurrentTenant currentTenant) : IConnectionAffinityProvider
{
    /// <inheritdoc />
    public string? AffinityKey => currentTenant.Id?.ToString();
}
