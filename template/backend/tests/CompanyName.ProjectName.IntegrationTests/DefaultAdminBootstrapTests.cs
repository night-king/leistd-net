#if (LocalIdentity)
using System.Net;
#if (IncludeMultiTenancy)
using CompanyName.ProjectName.Domain.Users.DomainServices;
#endif
using CompanyName.ProjectName.Domain.Users.Entities;
using CompanyName.ProjectName.Application.Users.Options;
using CompanyName.ProjectName.Domain.Users.Policies;
using Leistd.Ddd.Domain.Repositories;
#if (IncludeMultiTenancy)
using Leistd.MultiTenancy.Context;
#endif
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CompanyName.ProjectName.IntegrationTests;

/// <summary>默认管理员只在需要创建时才要口令：克隆即可运行，已有管理员的部署不必再提供，首次部署漏配仍然启动失败。</summary>
public sealed class DefaultAdminBootstrapTests(ProjectWebApplicationFactory factory)
    : IClassFixture<ProjectWebApplicationFactory>
{
    private const string PasswordKey = $"{DefaultAdminOptions.SectionName}:Password";

    // 开发配置自带的演示口令满足策略，新库用它建出管理员并能登录——这就是克隆后首次运行的路径
    [Fact]
    public async Task A_fresh_database_creates_the_admin_from_the_development_password()
    {
        var developmentPassword = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json")
            .AddJsonFile("appsettings.Development.json")
            .Build()[PasswordKey];
        Assert.True(PasswordPolicy.IsAcceptable(developmentPassword));

        using var host = CreateHost(freshDatabase: true, password: developmentPassword);
        using var session = await ProjectWebApplicationFactory.LoginAsync(host, "admin", developmentPassword!);

        Assert.Equal(HttpStatusCode.OK, (await session.Client.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    // 已有超级管理员：不读口令，缺失也照常启动
    [Fact]
    public async Task An_existing_admin_starts_without_a_password()
    {
        _ = factory.Services;

        using var host = CreateHost(freshDatabase: false, password: "");
        using var session = await ProjectWebApplicationFactory.LoginAsync(
            host, "admin", ProjectWebApplicationFactory.TestAdminPassword);

        Assert.Equal(HttpStatusCode.OK, (await session.Client.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    // 首次创建管理员却没有口令：启动失败，报错指明缺的是哪个键
    [Theory]
    [InlineData("")]
    [InlineData("elevenchars")]
    public void Creating_the_first_admin_without_a_usable_password_fails_startup(string password)
    {
        using var host = CreateHost(freshDatabase: true, password: password);

        var exception = Assert.ThrowsAny<Exception>(() => host.Services);

        Assert.Contains(PasswordKey, Flatten(exception));
    }

    // 同名普通用户已存在且库里没有超级管理员：把它标记为超级管理员，不创建、也不需要口令
    [Fact]
    public async Task An_existing_user_with_the_configured_name_is_promoted_without_a_password()
    {
        using var host = CreateHost(freshDatabase: true, password: "", username: "boss",
            configureServices: services => services.Insert(0,
                ServiceDescriptor.Singleton<IHostedService, SeedOrdinaryUser>()));

        await using var scope = host.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<IRepository<User, Guid>>();
        var boss = await users.GetFirstAsync(user => user.Username == "boss");

        Assert.NotNull(boss);
        Assert.True(boss.IsSuperAdmin);
    }

#if (IncludeMultiTenancy)
    // 提升已有用户与新建超管共用同一道宿主守卫：租户上下文里提升直接拒绝，用户不被标记
    [Fact]
    public async Task Promoting_a_user_to_super_admin_inside_a_tenant_is_rejected()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var userDomainService = scope.ServiceProvider.GetRequiredService<UserDomainService>();
        var currentTenant = scope.ServiceProvider.GetRequiredService<ICurrentTenant>();
        var user = new User("promote-target", "promote-target@example.test");

        using (currentTenant.Change(Guid.CreateVersion7()))
        {
            Assert.Throws<InvalidOperationException>(() => userDomainService.PromoteToSuperAdmin(user));
        }

        Assert.False(user.IsSuperAdmin);

        userDomainService.PromoteToSuperAdmin(user);
        Assert.True(user.IsSuperAdmin);
    }

#endif
    private WebApplicationFactory<Program> CreateHost(
        bool freshDatabase,
        string? password,
        string username = "admin",
        Action<IServiceCollection>? configureServices = null)
        => factory.WithWebHostBuilder(builder =>
        {
            if (freshDatabase)
            {
                // 派生宿主默认沿用父宿主的库；要"还没有管理员"的库就另克隆一份
                builder.UseSetting("ConnectionStrings:Default", PostgreSqlTestDatabase.CreateDatabase());
            }

            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [$"{DefaultAdminOptions.SectionName}:Username"] = username,
                    [PasswordKey] = password,
                }));
            if (configureServices is not null)
            {
                builder.ConfigureTestServices(configureServices);
            }
        });

    private static string Flatten(Exception exception)
    {
        var messages = new List<string>();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
            if (current is AggregateException aggregate)
            {
                messages.AddRange(aggregate.InnerExceptions.Select(inner => inner.Message));
            }
        }

        return string.Join(Environment.NewLine, messages);
    }

    // 先于管理员初始化运行：造出"有同名普通用户、没有超级管理员"的库
    private sealed class SeedOrdinaryUser(IServiceProvider services) : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await using var scope = services.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<IRepository<User, Guid>>();
            await users.InsertAsync(new User("boss", "boss@example.test"), cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
#endif
