using Leistd.Data.Connections;
using Leistd.MultiTenancy.ConnectionStrings;
using Leistd.MultiTenancy.Context;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Leistd.MultiTenancy.Tests.TestDoubles;

namespace Leistd.MultiTenancy.Tests.Core;

/// <summary>运行时的物理库清单：宿主库在前，每个物理库只出现一次，连接串不出现在结果里。</summary>
/// <remarks>
/// 清单来自库目录而不是迁移目标：迁移目标带明文连接串、要 DDL 身份，
/// 而逐库作业只需要"有哪些库、用哪个租户进得去"，两者权限不是一回事。
/// </remarks>
public class TenantDatabaseEnumeratorTests
{
    private const string HostConnection = "Host=db;Database=host";
    private const string OtherConnection = "Host=db;Database=other";

    private static readonly Guid First = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Second = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid Third = Guid.Parse("00000000-0000-0000-0000-000000000003");

    private static string HostFingerprint => TenantDatabaseFingerprint.Of(HostConnection);

    // 标准分库形态：路由标记 + 目录 + 解析器 + 租户上下文，四样齐全
    private static TenantDatabaseEnumerator Create(ITenantDatabaseDirectory directory)
        => new(Routing, directory, new FixedResolver(HostConnection), new SettableCurrentTenant());

    private static TenantConnectionRouting Routing => TenantConnectionRouting.Instance;

    /// <summary>没有租户分库时只剩宿主库，且指纹是宿主连接算出来的，不是占位符。</summary>
    [Fact]
    public async Task Without_dedicated_databases_only_the_host_database_is_listed()
    {
        var set = await Create(new FixedDirectory(TenantDatabaseListResult.Empty))
            .GetDatabasesAsync("Default", activeOnly: true);

        var host = Assert.Single(set.Databases);
        Assert.Null(host.TenantId);
        Assert.Equal(HostFingerprint, host.Fingerprint);
        Assert.Empty(set.FailedTenants);
    }

    /// <summary>宿主项不带住户清单：那份清单等于共享库的租户数，且要跨 HTTP 边界。</summary>
    /// <remarks>进宿主库用的是宿主配置，不需要某个租户；要查"谁住在宿主库"查连接登记表。</remarks>
    [Fact]
    public async Task The_host_entry_does_not_carry_its_tenants()
    {
        var directory = new FixedDirectory(new TenantDatabaseListResult([], []));

        var set = await Create(directory).GetDatabasesAsync("Default", activeOnly: true);

        var host = Assert.Single(set.Databases);
        Assert.Empty(host.TenantIds);
    }

    /// <summary>租户显式登记的连接与宿主相同时，并入宿主库，不另列一项。</summary>
    /// <remarks>
    /// 宿主库若用固定指纹（如 <c>"host"</c>），永远不等于任何真实指纹，
    /// 这种租户就会被列成第二个库——归档、清理会在同一个物理库上执行两遍。
    /// </remarks>
    [Fact]
    public async Task A_tenant_connection_equal_to_the_host_is_merged_into_the_host_database()
    {
        var directory = new FixedDirectory(new TenantDatabaseListResult(
            [new TenantDatabaseEntry(HostFingerprint, [First])],
            []));

        var set = await Create(directory).GetDatabasesAsync("Default", activeOnly: true);

        // 同指纹的那一项被丢掉，物理库只剩宿主这一项
        var host = Assert.Single(set.Databases);
        Assert.Null(host.TenantId);
        Assert.Equal(HostFingerprint, host.Fingerprint);
    }

    /// <summary>连接确实不同的租户仍然单独成库。</summary>
    [Fact]
    public async Task A_tenant_connection_that_differs_from_the_host_stays_separate()
    {
        var directory = new FixedDirectory(new TenantDatabaseListResult(
            [new TenantDatabaseEntry(TenantDatabaseFingerprint.Of(OtherConnection), [Third])],
            []));

        var set = await Create(directory).GetDatabasesAsync("Default", activeOnly: true);

        Assert.Equal([null, Third], set.Databases.Select(database => database.TenantId));
        Assert.Equal([Third], set.Databases[1].TenantIds);
    }

    /// <summary>共用一个独立库的租户只列一次，代表稳定，且清单带出全部住户。</summary>
    /// <remarks>按租户逐个处理会把同一个库跑多遍；住户清单是运维面板要展示的。</remarks>
    [Fact]
    public async Task Tenants_sharing_a_database_are_listed_once_and_carry_their_members()
    {
        var directory = new FixedDirectory(new TenantDatabaseListResult(
            [
                new TenantDatabaseEntry(TenantDatabaseFingerprint.Of(OtherConnection), [First, Second]),
                new TenantDatabaseEntry(TenantDatabaseFingerprint.Of("Host=db;Database=alone"), [Third])
            ],
            []));

        var set = await Create(directory).GetDatabasesAsync("Default", activeOnly: true);

        Assert.Equal([null, First, Third], set.Databases.Select(database => database.TenantId));
        Assert.Equal([First, Second], set.Databases[1].TenantIds);
    }

    /// <summary>解析不出连接的租户随清单带回，不抛——坏掉一个租户不该让整轮作业不执行。</summary>
    [Fact]
    public async Task Unresolved_tenants_come_back_with_the_list_instead_of_throwing()
    {
        var directory = new FixedDirectory(new TenantDatabaseListResult(
            [new TenantDatabaseEntry(TenantDatabaseFingerprint.Of(OtherConnection), [Third])],
            [new TenantDatabaseFailure(First, "key ring mismatch")]));

        var set = await Create(directory).GetDatabasesAsync("Default", activeOnly: true);

        Assert.Equal([null, Third], set.Databases.Select(database => database.TenantId));
        Assert.Equal(First, Assert.Single(set.FailedTenants).TenantId);
    }

    /// <summary>没有租户路由标记就按单库走，哪怕目录和通用解析器都在场。</summary>
    /// <remarks>
    /// 两种合法形态都落在这里：只持有控制库、自己不分库的服务（目录跟着控制库的存储注册，
    /// 而它没装 <c>AddLocalTenantConnectionResolution</c>）；以及单库项目自己实现
    /// <see cref="IConnectionStringResolver"/> 去配置中心或密钥服务取连接串——那个契约写明
    /// <b>与多租户无关</b>，随包文档就是这么示范的。
    /// <para>判据只认 <c>TenantConnectionRouting</c>，不从目录或解析器的在场与否推断：
    /// 那两个契约都没承诺"本进程分库"这个含义。</para>
    /// <para>这一支不做指纹比对也不会让同一个库被跑两遍——它压根没有第二个库可列。</para>
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Without_the_routing_marker_a_directory_and_a_plain_resolver_still_mean_a_single_database(bool withResolver)
    {
        var directory = new FixedDirectory(new TenantDatabaseListResult(
            [new TenantDatabaseEntry(TenantDatabaseFingerprint.Of(OtherConnection), [Third])],
            []));

        var set = await new TenantDatabaseEnumerator(
                routing: null,
                directory,
                withResolver ? new FixedResolver(OtherConnection) : null,
                withResolver ? new SettableCurrentTenant() : null)
            .GetDatabasesAsync("Default", activeOnly: true);

        Assert.Equal([null], set.Databases.Select(database => database.TenantId));
        Assert.Empty(Assert.Single(set.Databases).Fingerprint);
    }

    /// <summary>有标记却缺解析器、目录或租户上下文：抛出，不退化成单库。</summary>
    /// <remarks>
    /// 结果上与上一条长得一样（都没有独立库可列），后果却相反：那是把<b>每一个</b>独立库永久跳过，
    /// 而归档、清理照样报成功——没有任何信号，直到有人去查那些库为什么没动过。
    /// 标准注册路径四样一起给，走不到这里；自定义存储的宿主漏注册目录时正是这一档。
    /// </remarks>
    [Theory]
    [InlineData("resolver")]
    [InlineData("directory")]
    [InlineData("currentTenant")]
    public async Task The_routing_marker_without_its_collaborators_fails_instead_of_degrading_to_a_single_database(string missing)
    {
        var currentTenant = new SettableCurrentTenant();
        var enumerator = new TenantDatabaseEnumerator(
            Routing,
            missing == "directory" ? null : new FixedDirectory(TenantDatabaseListResult.Empty),
            missing == "resolver" ? null : new FixedResolver(HostConnection) { CurrentTenant = currentTenant },
            missing == "currentTenant" ? null : currentTenant);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => enumerator.GetDatabasesAsync("Default", activeOnly: true));
    }

    /// <summary>宿主连接在宿主视角下解析：带着租户上下文会拿到那个租户的库。</summary>
    [Fact]
    public async Task The_host_connection_is_resolved_from_the_host_perspective()
    {
        var currentTenant = new SettableCurrentTenant { Id = First };
        var resolver = new FixedResolver(HostConnection) { CurrentTenant = currentTenant };

        await new TenantDatabaseEnumerator(Routing, new FixedDirectory(TenantDatabaseListResult.Empty), resolver, currentTenant)
            .GetDatabasesAsync("Default", activeOnly: true);

        Assert.Null(resolver.TenantIdWhenResolved);
        Assert.Equal([null], currentTenant.Observed);
        // 用完必须还原，否则后续代码会以为自己还在宿主视角
        Assert.Equal(First, currentTenant.Id);
    }

    /// <summary>启用过滤原样传给目录：要不要算上停用租户由调用方决定，枚举器不替它选。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_active_filter_is_passed_through(bool activeOnly)
    {
        var directory = new FixedDirectory(TenantDatabaseListResult.Empty);

        await Create(directory).GetDatabasesAsync("Default", activeOnly);

        Assert.Equal(activeOnly, directory.LastActiveOnly);
    }

    /// <summary>没有注册任何租户连接解析时，核心注册给出的清单只有宿主库。</summary>
    /// <remarks>
    /// 单库部署与内存库测试都是这种形态。枚举器若只随连接解析注册，
    /// 宿主在单库模式下解析不到它，只好自己写一个"只有宿主库"的实现并按模式分支注册。
    /// </remarks>
    [Fact]
    public async Task Without_tenant_connection_resolution_only_the_host_database_is_listed()
    {
        using var provider = new ServiceCollection().AddMultiTenancyCore().BuildServiceProvider();

        var set = await provider.GetRequiredService<ITenantDatabaseEnumerator>()
            .GetDatabasesAsync("Default", activeOnly: true);

        var host = Assert.Single(set.Databases);
        Assert.Null(host.TenantId);
    }

    private sealed class FixedDirectory(TenantDatabaseListResult result) : ITenantDatabaseDirectory
    {
        public bool? LastActiveOnly { get; private set; }

        public Task<TenantDatabaseListResult> GetDatabasesAsync(
            string name,
            bool activeOnly,
            CancellationToken cancellationToken = default)
        {
            LastActiveOnly = activeOnly;
            return Task.FromResult(result);
        }
    }

    private sealed class FixedResolver(string connectionString) : IConnectionStringResolver
    {
        public Guid? TenantIdWhenResolved { get; private set; }

        public ICurrentTenant? CurrentTenant { get; set; }

        public Task<string> ResolveAsync(string connectionStringName, CancellationToken cancellationToken = default)
        {
            TenantIdWhenResolved = CurrentTenant?.Id;
            return Task.FromResult(connectionString);
        }
    }
}
