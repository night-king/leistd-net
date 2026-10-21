namespace Leistd.MultiTenancy.ConnectionStrings;

// 迁移目标一律从连接存储按名字读；本地（EF）与远端（HTTP）形态的差异由存储的注册决定。
internal sealed class TenantMigrationTargetProvider(ITenantConnectionConfigurationStore connectionStore)
    : ITenantMigrationTargetProvider
{
    public async Task<TenantMigrationTargetSet> GetDedicatedTargetsAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        var list = await connectionStore.GetListAsync(name, cancellationToken);

        // 存储已按名字解析、排除无登记租户并单列失败租户；这里按物理库合并。
        return new TenantMigrationTargetSet(
            [
                .. list.Connections
                    .Select(x => new TenantMigrationTarget(x.TenantId, x.ConnectionString))
                    .GroupBy(x => x.Fingerprint, StringComparer.Ordinal)
                    .Select(group => group.MinBy(x => x.TenantId)!)
            ],
            list.FailedTenants);
    }
}
