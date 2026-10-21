using System.Net;
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

/// <summary>租户删除端点经正式 <c>MapTenantManagement</c> 入口与真实用例服务：删除幂等，不存在与重复删除都是 204 且不发事件。</summary>
public sealed class TenantEndpointDeletionTests : IAsyncLifetime
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
                    // 本组只验证删除语义，授权口径由 TenantEndpointMappingTests 钉住
                    services.AddAuthorization(options => options.AddPolicy(Policy, policy => policy.RequireAssertion(_ => true)));
                })
                .Configure(app =>
                {
                    app.UseExceptionHandler();
                    app.UseRouting();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapGroup("/tenants").MapTenantManagement(options =>
                    {
                        options.ReadPolicy = Policy;
                        options.CreatePolicy = Policy;
                        options.UpdatePolicy = Policy;
                        options.DeletePolicy = Policy;
                    }));
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

    [Fact]
    public async Task Deleting_a_missing_tenant_returns_no_content_without_an_event()
    {
        var response = await _client.DeleteAsync($"/tenants/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(_events.Published);
    }

    [Fact]
    public async Task Deleting_the_same_tenant_twice_returns_no_content_and_publishes_one_deletion()
    {
        var tenant = await Tenants().CreateAsync(new CreateTenantInputDto { Name = "acme" });
        var other = await Tenants().CreateAsync(new CreateTenantInputDto { Name = "contoso" });
        _events.Published.Clear();

        var first = await _client.DeleteAsync($"/tenants/{tenant.Id}");
        var second = await _client.DeleteAsync($"/tenants/{tenant.Id}");

        Assert.Equal((HttpStatusCode.NoContent, HttpStatusCode.NoContent), (first.StatusCode, second.StatusCode));
        var deleted = Assert.IsType<TenantChangedEvent>(Assert.Single(_events.Published));
        Assert.Equal((tenant.Id, TenantChangeKind.Deleted), (deleted.TenantId, deleted.Change));
        Assert.Equal(other.Id, (await Tenants().GetAsync(other.Id)).Id);
    }

    private ITenantManagementService Tenants() => _host.Services.GetRequiredService<ITenantManagementService>();

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ConfigureMultiTenancy();
    }
}
