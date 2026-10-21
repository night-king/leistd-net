using Leistd.MultiTenancy.ConnectionStrings;
using Leistd.MultiTenancy.Context;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Leistd.MultiTenancy.Tests.Core;

/// <summary>逐库执行：回调运行在对应库的租户上下文里，一个库失败不影响其余的库。</summary>
/// <remarks>
/// 回调里若还处在宿主上下文，经提供器取到的永远是宿主库，独立库租户的数据被静默漏掉；
/// 若一个库的异常冒出来，排在它后面的库这一轮都不会被处理。
/// </remarks>
public class TenantDatabaseRunnerTests
{
    private static readonly Guid First = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Second = Guid.Parse("00000000-0000-0000-0000-000000000002");

    [Fact]
    public async Task Each_database_runs_in_its_tenant_context_and_failures_are_isolated()
    {
        await using var provider = new ServiceCollection()
            .AddLogging()
            .AddMultiTenancyCore()
            .AddSingleton<ITenantDatabaseEnumerator>(new FixedEnumerator(
                TenantDatabase.ForHost("host"), new TenantDatabase(First, "a", []), new TenantDatabase(Second, "b", [])))
            .BuildServiceProvider();
        var currentTenant = provider.GetRequiredService<ICurrentTenant>();
        var seen = new List<Guid?>();

        var result = await provider.GetRequiredService<ITenantDatabaseRunner>().ForEachDatabaseAsync("Default", activeOnly: false, (database, _) =>
        {
            seen.Add(currentTenant.Id);
            return database.TenantId == First ? throw new InvalidOperationException("unreachable") : Task.CompletedTask;
        });

        Assert.Equal([null, First, Second], seen);
        Assert.Equal(3, result.Databases);
        Assert.Equal(First, Assert.Single(result.FailedDatabases).TenantId);
        Assert.Null(currentTenant.Id);
    }

    private sealed class FixedEnumerator(params TenantDatabase[] databases) : ITenantDatabaseEnumerator
    {
        public Task<TenantDatabaseSet> GetDatabasesAsync(string name, bool activeOnly, CancellationToken cancellationToken = default)
            => Task.FromResult(new TenantDatabaseSet(databases, []));
    }
}
