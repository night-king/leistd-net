using Leistd.Data.Connections;
using Leistd.MultiTenancy.Context;

namespace Leistd.MultiTenancy.ConnectionStrings;

// 清单取自库目录（不含连接串），再与宿主库合并。
// 是否逐库只看 TenantConnectionRouting 标记（理由见该类型）。有标记却缺解析器、目录或租户上下文是组合不完整，
// 必须抛：否则所有独立库被静默跳过，归档、清理照样报成功。
// 宿主库参与去重：与租户登记指向同一连接时指纹相等，合并成一项，避免同一个库执行两遍。
internal sealed class TenantDatabaseEnumerator(
    TenantConnectionRouting? routing = null,
    ITenantDatabaseDirectory? directory = null,
    IConnectionStringResolver? connectionStringResolver = null,
    ICurrentTenant? currentTenant = null)
    : ITenantDatabaseEnumerator
{
    public async Task<TenantDatabaseSet> GetDatabasesAsync(
        string name,
        bool activeOnly,
        CancellationToken cancellationToken = default)
    {
        // 单库模式（含宿主自己注册的通用 IConnectionStringResolver）：只有宿主库，指纹不参与判定，留空串。
        if (routing is null)
        {
            return new TenantDatabaseSet([TenantDatabase.ForHost(string.Empty)], []);
        }

        if (connectionStringResolver is null || directory is null || currentTenant is null)
        {
            throw new InvalidOperationException(
                $"Tenant connection routing is registered, but enumerating the databases for '{name}' also needs "
                + $"{nameof(IConnectionStringResolver)}, {nameof(ITenantDatabaseDirectory)} and {nameof(ICurrentTenant)}. "
                + "Without them every dedicated database would be skipped while per-database jobs report success.");
        }

        var hostFingerprint = await ResolveHostFingerprintAsync(name, cancellationToken);
        var listed = await directory.GetDatabasesAsync(name, activeOnly, cancellationToken);

        // 指纹等于宿主的登记就是宿主库，已由宿主项代表；进该库用宿主配置即可。
        var dedicated = listed.Databases
            .Where(entry => !string.Equals(entry.Fingerprint, hostFingerprint, StringComparison.Ordinal))
            .Select(entry => new TenantDatabase(
                entry.TenantIds.Count > 0 ? entry.TenantIds[0] : null,
                entry.Fingerprint,
                entry.TenantIds));

        return new TenantDatabaseSet(
            [TenantDatabase.ForHost(hostFingerprint), .. dedicated],
            listed.FailedTenants);
    }

    // 宿主连接必须在宿主视角下解析：解析器是租户感知的，带着租户上下文调会拿到那个租户的库。
    private async Task<string> ResolveHostFingerprintAsync(string name, CancellationToken cancellationToken)
    {
        using (currentTenant!.Change(null))
        {
            var connectionString = await connectionStringResolver!.ResolveAsync(name, cancellationToken);
            return TenantDatabaseFingerprint.Of(connectionString);
        }
    }
}
