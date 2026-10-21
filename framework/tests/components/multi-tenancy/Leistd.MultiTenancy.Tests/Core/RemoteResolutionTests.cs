using Leistd.MultiTenancy.Exceptions;
using Xunit;
using Leistd.MultiTenancy.Tests.TestDoubles;

namespace Leistd.MultiTenancy.Tests.Core;

/// <summary>
/// 远端解析的三级落点：不分库走服务自己的配置，登记了就按"精确名 → 默认名"取，
/// 登记过却两者都缺则失败关闭。远端下发的是已解密的连接串，本服务不接触密钥环。
/// </summary>
public sealed class RemoteResolutionTests : IDisposable
{
    private readonly RemoteHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Without_a_tenant_the_host_connection_is_used()
    {
        Assert.Equal(RemoteHost.DefaultConnection, await _host.ResolveAsync());
        Assert.Equal(0, _host.Source.RuntimeCalls);
    }

    // 一条连接都没登记 = 该租户不单独分库，用这个服务自己配置的库
    [Fact]
    public async Task A_tenant_without_any_registered_connection_uses_the_host_connection()
    {
        _host.Tenant.Id = Guid.NewGuid();

        Assert.Equal(RemoteHost.DefaultConnection, await _host.ResolveAsync());
    }

    [Fact]
    public async Task An_exact_name_hit_is_used()
    {
        _host.Tenant.Id = Guid.NewGuid();
        _host.Source.Lookup = (id, name) => ScriptedRemoteSource.Hit(id, name, "Data Source=acme-crm");

        Assert.Equal("Data Source=acme-crm", await _host.ResolveAsync("Crm"));
    }

    // 一租户一库、各服务不同 schema：只登记 default 一条，所有服务都回落到它
    [Fact]
    public async Task A_default_named_registration_is_the_fallback_for_any_name()
    {
        _host.Tenant.Id = Guid.NewGuid();
        _host.Source.Lookup = (id, _) => ScriptedRemoteSource.Hit(id, "default", "Data Source=acme");

        Assert.Equal("Data Source=acme", await _host.ResolveAsync("Crm"));
    }

    // 分库租户缺少当前连接名且无 default 时须失败关闭，回落本服务配置会把写入落到错误的物理库。
    [Fact]
    public async Task A_registered_tenant_missing_this_name_is_refused()
    {
        _host.Tenant.Id = Guid.NewGuid();
        _host.Source.Lookup = (id, _) => ScriptedRemoteSource.RegisteredButMissing(id);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _host.ResolveAsync("Crm"));

        Assert.Contains("Crm", error.Message, StringComparison.Ordinal);
    }

    // 租户不存在或已删除：404，且同样不回落
    [Fact]
    public async Task An_unknown_tenant_is_refused()
    {
        _host.Tenant.Id = Guid.NewGuid();
        _host.Source.Lookup = (_, _) => null;

        await Assert.ThrowsAsync<TenantNotFoundException>(() => _host.ResolveAsync());
    }

    // 错误的响应不能造成跨租户数据访问：校验在使用与写缓存之前；拒绝消息不带出对方的连接串
    [Fact]
    public async Task A_response_for_another_tenant_is_refused_and_not_cached()
    {
        _host.Tenant.Id = Guid.NewGuid();
        var other = Guid.NewGuid();
        _host.Source.Lookup = (_, name) => ScriptedRemoteSource.Hit(other, name, "Data Source=other;Password=other-secret");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _host.ResolveAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => _host.ResolveAsync());

        Assert.Equal(2, _host.Source.RuntimeCalls);
        Assert.DoesNotContain("other-secret", error.ToString());
    }

    // 连接名就是 DbContext 的连接名，不再有"只有 Default 参与路由"的白名单
    [Fact]
    public async Task Any_connection_name_participates_in_tenant_routing()
    {
        _host.Tenant.Id = Guid.NewGuid();
        _host.Source.Lookup = (id, name) => ScriptedRemoteSource.Hit(id, name, $"Data Source={name}");

        Assert.Equal("Data Source=foundation", await _host.ResolveAsync("Foundation"));
    }

    // 传给存储的必须是归一化后的名字：管理员填 Crm 还是 crm 命中同一行
    [Fact]
    public async Task The_store_is_asked_with_the_normalized_name()
    {
        _host.Tenant.Id = Guid.NewGuid();
        _host.Source.Lookup = (id, name) => ScriptedRemoteSource.Hit(id, name, "Data Source=acme");

        await _host.ResolveAsync("Crm");

        Assert.Equal(["crm"], _host.Source.RequestedNames);
    }

    // 宿主连接按名字找不到时回落 Default：业务项目把上下文改名后不用改部署
    [Fact]
    public async Task The_host_connection_falls_back_to_the_default_name()
    {
        using var host = new RemoteHost();
        host.Tenant.Id = Guid.NewGuid();

        Assert.Equal(RemoteHost.DefaultConnection, await host.ResolveAsync("Crm"));
    }

    // 配了同名连接就优先用它，不再回落 Default
    [Fact]
    public async Task A_named_host_connection_wins_over_the_default_name()
    {
        using var host = new RemoteHost(namedConnection: "Data Source=crm-host");
        host.Tenant.Id = Guid.NewGuid();

        Assert.Equal("Data Source=crm-host", await host.ResolveAsync("Crm"));
    }

    [Fact]
    public async Task A_missing_host_connection_fails_instead_of_returning_null()
    {
        using var host = new RemoteHost(defaultConnection: null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.ResolveAsync());
    }

    // 名字本身不合法是配置错误，不该被当成"查不到"
    [Theory]
    [InlineData("crm_db")]
    [InlineData("Crm Db")]
    [InlineData("crm.db")]
    public async Task An_invalid_connection_name_is_rejected(string name)
    {
        _host.Tenant.Id = Guid.NewGuid();

        await Assert.ThrowsAsync<ArgumentException>(() => _host.ResolveAsync(name));
        Assert.Equal(0, _host.Source.RuntimeCalls);
    }
}
