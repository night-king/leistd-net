using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Leistd.DependencyInjection.DynamicProxy.Registration;
using Leistd.EventBus.Abstractions;
using Leistd.ExceptionHandling.AspNetCore;
using Leistd.MultiTenancy.AspNetCore.Endpoints;
using Leistd.MultiTenancy.EntityFrameworkCore;
using Leistd.MultiTenancy.Management;
using Leistd.MultiTenancy.Management.Dtos;
using Leistd.MultiTenancy.Management.Events;
using Leistd.TestBase.Doubles;
using Leistd.UnitOfWork;
using Leistd.UnitOfWork.EntityFrameworkCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Leistd.MultiTenancy.Tests.AspNetCore;

/// <summary>租户与连接端点的请求体验证：经正式 <c>Map*</c> 入口与真实用例服务，缺字段按字段报 400，且没有写入、没有事件。</summary>
/// <remarks>
/// Minimal API 不自动执行 DataAnnotations，验证由用例入口完成；漏掉入口验证时，缺 <c>isActive</c> 的请求会被当成停用。
/// <c>expectedVersion</c> 是"必须出现、可以为 null"的存在性字段：缺键在反序列化阶段拒绝，显式 null 才是首次登记。
/// </remarks>
public sealed class TenantEndpointInputValidationTests : IAsyncLifetime
{
    private const string Policy = "tenants";
    private readonly RecordingEventBus _events = new();
    private SqliteConnection _connection = default!;
    private IHost _host = default!;
    private HttpClient _client = default!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        _host = await new HostBuilder()
            .UseServiceProviderFactory(new DynamicProxyServiceRegistrationCallbackFactory())
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddDbContext<TestDbContext>(options => options.UseSqlite(_connection));
                    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
                    services.AddUnitOfWork();
                    services.AddUnitOfWorkEfCore();
                    services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
                    services.AddMultiTenancyEfCore<TestDbContext>();
                    services.AddTenantManagement();
                    services.AddSingleton<ILocalEventBus>(_events);
                    services.AddGlobalExceptionHandler();
                    // 本组只验证请求体，授权口径由 TenantEndpointMappingTests 钉住
                    services.AddAuthorization(options => options.AddPolicy(Policy, policy => policy.RequireAssertion(_ => true)));
                })
                .Configure(app =>
                {
                    app.UseExceptionHandler();
                    app.UseRouting();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGroup("/tenants").MapTenantManagement(options =>
                        {
                            options.ReadPolicy = Policy;
                            options.CreatePolicy = Policy;
                            options.UpdatePolicy = Policy;
                            options.DeletePolicy = Policy;
                        });
                        endpoints.MapGroup("/tenant-connections").MapTenantConnections(options => options.ManagePolicy = Policy);
                    });
                }))
            .StartAsync();

        await using (var scope = _host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<TestDbContext>().Database.EnsureCreatedAsync();
        }

        _client = _host.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
        await _connection.DisposeAsync();
    }

    [Theory]
    [InlineData("{}", "name")]
    [InlineData("""{"name":"acme","connections":[{"name":"default"}]}""", "connectionString")]
    [InlineData("""{"name":"acme","connections":[{"connectionString":"Host=acme"}]}""", "name")]
    public async Task Creating_with_a_missing_field_reports_the_field_and_creates_nothing(string body, string field)
    {
        var response = await SendAsync(HttpMethod.Post, "/tenants", body);

        await AssertFieldErrorAsync(response, field);
        Assert.Equal(0, (await Tenants().GetPagedAsync(new GetTenantPagedInputDto())).TotalCount);
        Assert.Empty(_events.Published);
    }

    [Fact]
    public async Task Updating_without_a_name_reports_the_field_and_changes_nothing()
    {
        var tenant = await CreateTenantAsync();

        var response = await SendAsync(HttpMethod.Put, $"/tenants/{tenant.Id}", """{"displayName":"Renamed"}""");

        await AssertFieldErrorAsync(response, "name");
        Assert.Null((await Tenants().GetAsync(tenant.Id)).DisplayName);
        Assert.Empty(_events.Published);
    }

    /// <summary>漏掉 <c>isActive</c> 不能被当成停用：按字段拒绝，租户保持启用。</summary>
    [Fact]
    public async Task Changing_activation_without_the_target_state_reports_the_field_and_keeps_the_tenant_active()
    {
        var tenant = await CreateTenantAsync();

        var response = await SendAsync(HttpMethod.Put, $"/tenants/{tenant.Id}/activation", "{}");

        await AssertFieldErrorAsync(response, "isActive");
        Assert.True((await Tenants().GetAsync(tenant.Id)).IsActive);
        Assert.Empty(_events.Published);
    }

    [Fact]
    public async Task An_explicit_false_deactivates_the_tenant()
    {
        var tenant = await CreateTenantAsync();

        var response = await SendAsync(HttpMethod.Put, $"/tenants/{tenant.Id}/activation", """{"isActive":false}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await response.Content.ReadFromJsonAsync<TenantOutputDto>())!.IsActive);
        Assert.False((await Tenants().GetAsync(tenant.Id)).IsActive);
        Assert.Equal(TenantChangeKind.ActivationChanged, Assert.IsType<TenantChangedEvent>(Assert.Single(_events.Published)).Change);
    }

    /// <summary>缺 <c>expectedVersion</c> 键不能被当成首次登记：在反序列化阶段拒绝（没有字段级错误），不写入。</summary>
    [Fact]
    public async Task Setting_a_connection_without_the_expected_version_key_is_rejected_without_writing()
    {
        var tenant = await CreateInactiveTenantAsync();

        var response = await SendAsync(HttpMethod.Put, $"/tenant-connections/{tenant.Id}/default", """{"connectionString":"Host=acme"}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await Connections().GetListAsync(tenant.Id));
        Assert.Empty(_events.Published);
    }

    [Fact]
    public async Task Setting_a_connection_without_a_connection_string_reports_the_field_and_writes_nothing()
    {
        var tenant = await CreateInactiveTenantAsync();

        var response = await SendAsync(HttpMethod.Put, $"/tenant-connections/{tenant.Id}/default", """{"expectedVersion":null}""");

        await AssertFieldErrorAsync(response, "connectionString");
        Assert.Empty(await Connections().GetListAsync(tenant.Id));
        Assert.Empty(_events.Published);
    }

    /// <summary>显式 null 是首次登记；带读到的版本是改写；拿过期版本改写是并发冲突，不写入。</summary>
    [Fact]
    public async Task An_explicit_null_registers_the_current_version_changes_and_a_stale_version_conflicts()
    {
        var tenant = await CreateInactiveTenantAsync();
        var url = $"/tenant-connections/{tenant.Id}/default";

        var registered = await SendAsync(HttpMethod.Put, url, """{"expectedVersion":null,"connectionString":"Host=a"}""");
        var changed = await SendAsync(HttpMethod.Put, url, """{"expectedVersion":1,"connectionString":"Host=b"}""");
        var stale = await SendAsync(HttpMethod.Put, url, """{"expectedVersion":1,"connectionString":"Host=c"}""");

        Assert.Equal(
            (HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.Conflict),
            (registered.StatusCode, changed.StatusCode, stale.StatusCode));
        Assert.Equal(2, Assert.Single(await Connections().GetListAsync(tenant.Id)).Version);
        Assert.Equal(
            [TenantConnectionChangeKind.Registered, TenantConnectionChangeKind.Changed],
            _events.Published.OfType<TenantConnectionChangedEvent>().Select(e => e.Change));
    }

    private ITenantManagementService Tenants() => _host.Services.GetRequiredService<ITenantManagementService>();

    private ITenantConnectionManagementService Connections() => _host.Services.GetRequiredService<ITenantConnectionManagementService>();

    private async Task<TenantOutputDto> CreateTenantAsync()
    {
        var tenant = await Tenants().CreateAsync(new CreateTenantInputDto { Name = "acme" });
        _events.Published.Clear();
        return tenant;
    }

    // 启用中的租户不能改变数据落点，连接登记要在停用态下做
    private async Task<TenantOutputDto> CreateInactiveTenantAsync()
    {
        var tenant = await CreateTenantAsync();
        await Tenants().SetActivationAsync(tenant.Id, new UpdateTenantActivationInputDto { IsActive = false });
        _events.Published.Clear();
        return tenant;
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string body)
        => _client.SendAsync(new HttpRequestMessage(method, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });

    private static async Task AssertFieldErrorAsync(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains(field, document.RootElement.GetProperty("errors").EnumerateArray()
            .Select(error => error.GetProperty("field").GetString()));
    }

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ConfigureMultiTenancy();
    }
}
