using System.Net;
using System.Text;
using Leistd.MultiTenancy.ConnectionStrings;
using Leistd.MultiTenancy.EntityFrameworkCore;
using Leistd.MultiTenancy.ServiceClient;
using Leistd.MultiTenancy.ServiceClient.Options;
using Leistd.MultiTenancy.ServiceClient.Stores;
using Leistd.ServiceClient.Exceptions;
using Leistd.TestBase.Doubles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Leistd.MultiTenancy.Tests.ServiceClient;

/// <summary>远端连接存储：按控制面端点的路由回源，只把"租户不存在"翻成 null，其余错误一律上抛。</summary>
/// <remarks>
/// 服务调用管道把非成功响应统一转成 <c>RemoteServiceException</c>：客户端若只捕获 Refit 的 <c>ApiException</c>，
/// "租户不存在返回 null"的分支永远不触发。这里用真实的错误形态断言。
/// </remarks>
public sealed class RemoteTenantConnectionStoreTests
{
    private static readonly Guid TenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private readonly CapturingHttpMessageHandler _handler = new();

    [Fact]
    public async Task A_runtime_lookup_is_asked_by_name_and_mapped()
    {
        _handler.Responder = _ => Json(HttpStatusCode.OK, $$$"""
            {"tenantId":"{{{TenantId}}}","hasAnyConnection":true,"connection":{"name":"crm","connectionString":"Host=crm","version":3}}
            """);

        var lookup = await Store("/api/v1/tenant-connections/").FindAsync(TenantId, "crm");

        Assert.Equal($"/api/v1/tenant-connections/runtime/{TenantId}", _handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("?name=crm", _handler.Requests[0].RequestUri!.Query);
        Assert.Equal((TenantId, true, "crm", "Host=crm", 3L),
            (lookup!.TenantId, lookup.HasAnyConnection, lookup.Connection!.Name, lookup.Connection.ConnectionString, lookup.Connection.Version));
    }

    [Fact]
    public async Task A_missing_tenant_is_null()
    {
        _handler.Responder = _ => Json(HttpStatusCode.NotFound, """{"status":404,"code":"Tenant:NotFound"}""");

        Assert.Null(await Store().FindAsync(TenantId, "crm"));
    }

    // 路由配错也是 404：翻成 null 会表现成"所有租户都不存在"，必须大声失败
    [Fact]
    public async Task A_404_that_is_not_a_missing_tenant_is_thrown()
    {
        _handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        var error = await Assert.ThrowsAsync<RemoteServiceException>(() => Store().FindAsync(TenantId, "crm"));

        Assert.Equal(404, error.RemoteStatusCode);
    }

    // 控制面不可达不能表现成"这个租户不存在"
    [Fact]
    public async Task Server_errors_are_thrown()
    {
        _handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

        await Assert.ThrowsAsync<RemoteServiceException>(() => Store().FindAsync(TenantId, "crm"));
    }

    // 控制面答了 200 却没有内容是对端故障：归为无效响应，经全局异常处理成 502，不能当成本服务内部错误
    [Theory]
    [InlineData("")]
    [InlineData("null")]
    public async Task An_empty_runtime_lookup_is_an_invalid_response(string body)
    {
        _handler.Responder = _ => Json(HttpStatusCode.OK, body);

        var error = await Assert.ThrowsAsync<ServiceClientException>(() => Store().FindAsync(TenantId, "crm"));

        Assert.Equal(ServiceClientFailureKind.InvalidResponse, error.FailureKind);
    }

    [Fact]
    public async Task Migration_targets_and_failed_tenants_are_listed_by_name()
    {
        var failed = Guid.NewGuid();
        _handler.Responder = _ => Json(HttpStatusCode.OK, $$"""
            {"connections":[{"tenantId":"{{TenantId}}","name":"default","connectionString":"Host=a"}],
             "failedTenants":[{"tenantId":"{{failed}}","reason":"no connection named 'crm'"}]}
            """);

        var list = await Store().GetListAsync("crm");

        Assert.Equal("/api/v1/tenant-connections/migration", _handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal(new TenantMigrationConnection(TenantId, "default", "Host=a"), Assert.Single(list.Connections));
        Assert.Equal(new TenantDatabaseFailure(failed, "no connection named 'crm'"), Assert.Single(list.FailedTenants));
    }

    // 空响应不能当成"没有目标"：迁移作业会以为一切正常，漏掉本该迁移的库
    [Fact]
    public async Task An_empty_migration_response_is_an_error_not_an_empty_list()
    {
        _handler.Responder = _ => Json(HttpStatusCode.OK, "null");

        var error = await Assert.ThrowsAsync<ServiceClientException>(() => Store().GetListAsync("crm"));

        Assert.Equal(ServiceClientFailureKind.InvalidResponse, error.FailureKind);
    }

    // 同理，空的库目录不能当成"没有库"：逐库作业会静默跳过全部租户库
    [Fact]
    public async Task An_empty_database_listing_is_an_error_not_an_empty_directory()
    {
        _handler.Responder = _ => Json(HttpStatusCode.OK, "null");

        var error = await Assert.ThrowsAsync<ServiceClientException>(() => Store().GetDatabasesAsync("crm", activeOnly: true));

        Assert.Equal(ServiceClientFailureKind.InvalidResponse, error.FailureKind);
    }

    [Fact]
    public async Task Migration_listing_server_errors_are_thrown()
    {
        _handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

        await Assert.ThrowsAsync<RemoteServiceException>(() => Store().GetListAsync("crm"));
    }

    [Fact]
    public void Registering_it_next_to_the_control_database_store_fails()
    {
        var services = new ServiceCollection().AddMultiTenancyEfCore<DbContext>();

        var error = Assert.Throws<InvalidOperationException>(
            () => services.AddRemoteTenantConnectionStore("Identity"));

        Assert.Contains("exactly one authoritative source", error.Message);
    }

    // 路由全靠相对地址回源：没有 BaseAddress 时组合照常成功，要到首个租户请求才以不带键名的 URI 错误暴露
    [Theory]
    [InlineData(null)]
    [InlineData("identity")]
    public void Missing_or_relative_base_address_is_rejected_with_the_configuration_key(string? baseAddress)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Leistd:ServiceClients:Identity:BaseAddress"] = baseAddress })
            .Build();
        using var provider = new ServiceCollection().AddLogging().AddSingleton<IConfiguration>(configuration)
            .AddRemoteTenantConnectionStore("Identity").Services
            .BuildServiceProvider();

        var error = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<RemoteTenantConnectionClientOptions>>().Value);

        Assert.Contains("Leistd:ServiceClients:Identity:BaseAddress", error.Message);
    }

    // 正例：校验落在注册路径实际绑定的那份选项上，而不是恒失败
    [Fact]
    public void An_absolute_base_address_passes_validation()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Leistd:ServiceClients:Identity:BaseAddress"] = "https://identity.test" })
            .Build();
        using var provider = new ServiceCollection().AddLogging().AddSingleton<IConfiguration>(configuration)
            .AddRemoteTenantConnectionStore("Identity").Services
            .BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<RemoteTenantConnectionClientOptions>>().Value;

        Assert.Equal("https://identity.test", options.BaseAddress);
    }

    [Fact]
    public void Database_directory_resolves_the_registered_control_plane_client()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Leistd:ServiceClients:Identity:BaseAddress"] = "https://identity.test"
        }).Build();
        using var provider = new ServiceCollection().AddLogging().AddSingleton<IConfiguration>(configuration)
            .AddRemoteTenantConnectionStore("Identity").Services.BuildServiceProvider();
        Assert.IsAssignableFrom<ITenantConnectionConfigurationStore>(provider.GetRequiredService<ITenantDatabaseDirectory>());
    }

    private RemoteTenantConnectionStore Store(string prefix = RemoteTenantConnectionClientOptions.DefaultRoutePrefix)
        => new(
            new HttpClient(_handler) { BaseAddress = new Uri("https://identity.test") },
            Microsoft.Extensions.Options.Options.Create(new RemoteTenantConnectionClientOptions { RoutePrefix = prefix }));

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, status == HttpStatusCode.OK ? "application/json" : "application/problem+json") };
}
