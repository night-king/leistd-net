using Leistd.MultiTenancy.Context;
using Microsoft.Extensions.Logging;

namespace Leistd.MultiTenancy.ConnectionStrings;

internal sealed class TenantDatabaseRunner(
    ITenantDatabaseEnumerator databaseEnumerator,
    ICurrentTenant currentTenant,
    ILogger<TenantDatabaseRunner> logger) : ITenantDatabaseRunner
{
    public async Task<TenantDatabaseRunResult> ForEachDatabaseAsync(
        string connectionStringName,
        bool activeOnly,
        Func<TenantDatabase, CancellationToken, Task> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        var set = await databaseEnumerator.GetDatabasesAsync(connectionStringName, activeOnly, cancellationToken);
        var failed = new List<TenantDatabase>();

        // 解析不出连接的租户只记不抛，不阻止其余库
        foreach (var unresolved in set.FailedTenants)
        {
            logger.LogError(
                "Tenant {TenantId} was skipped: its '{ConnectionName}' connection could not be resolved. {Reason}",
                unresolved.TenantId,
                connectionStringName,
                unresolved.Reason);
        }

        foreach (var database in set.Databases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using (currentTenant.Change(database.TenantId))
                {
                    await action(database, cancellationToken);
                }
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                // 一个库连不上不该让其余的库也跳过；失败的库计入结果，由调用方决定如何报告
                failed.Add(database);
                logger.LogError(exception, "Per-database work failed for {Database}.", database);
            }
        }

        return new TenantDatabaseRunResult(set.Databases.Count, failed, set.FailedTenants);
    }
}
