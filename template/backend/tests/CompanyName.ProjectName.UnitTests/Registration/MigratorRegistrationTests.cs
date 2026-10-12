using CompanyName.ProjectName.DbMigrator;
using CompanyName.ProjectName.DbMigrator.Runners;
#if (RemoteTokenAuth || (LocalIdentity && IncludeMultiTenancy))
using CompanyName.ProjectName.Infrastructure;
using Leistd.Lock.Redis.Options;
#endif
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
#if (RemoteTokenAuth || (LocalIdentity && IncludeMultiTenancy))
using Microsoft.Extensions.Options;
#endif

namespace CompanyName.ProjectName.UnitTests.Registration;

/// <summary>迁移作业的注册面：调用 DbMigrator 的同一组合，按开发环境的方式构建容器。</summary>
/// <remarks>
/// 开发环境的宿主在构建期校验每条注册的依赖（<c>ValidateOnBuild</c>）。迁移作业只注册持久化；
/// 运行期组件依赖只在 API 里注册的当前用户与权限主体，混进来时生产环境照常迁移，本机按 README 跑迁移却在构建期失败。
/// </remarks>
public class MigratorRegistrationTests
{
    [Fact]
    public void The_migrator_registration_passes_build_time_validation()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // 真实库的注册路径（租户连接解析等）；构建容器不连库
                ["ConnectionStrings:Default"] = "Host=localhost;Database=migrator-registration;Username=postgres",
#if (LocalIdentity)
                ["DataProtection:KeysPath"] = Path.Combine(Path.GetTempPath(), $"migrator-keys-{Guid.NewGuid():N}"),
#endif
#if (RemoteTokenAuth)
                // 迁移目标回源身份服务，构造远端存储时即校验；部署时由 compose 以必填变量提供
                ["Leistd:ServiceClients:Identity:BaseAddress"] = "https://identity.example",
#endif
            })
            .Build();
        var environment = new DevelopmentEnvironment();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IHostEnvironment>(environment);
        services.AddMigratorServices(configuration, environment);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<DatabaseMigrationRunner>());
    }

#if (RemoteTokenAuth || (LocalIdentity && IncludeMultiTenancy))
    // 管理员命令与 API 一样在生产以外开启构建期校验：组合里混进只在 API 注册的能力（实时推送、当前用户的设置等）时，
    // 本机与测试环境的命令会在启动时失败。这里不连数据库与 Redis，开了实时等能力的形态也在这一层暴露
    [Fact]
    public void The_admin_command_registration_passes_build_time_validation()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(LockConfiguration([])).Build();

        // 执行器是 internal 类型、不在这里解析：构建期校验会逐条检查它及其依赖的注册
        var failure = Record.Exception(() => AdminCommandServices(configuration).BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        }).Dispose());

        Assert.Null(failure);
    }

    // 管理员命令与 API 里的初始化（开通、启动初始化）靠同一把 Redis 锁互斥：两边的锁键前缀必须相同。
    // API 一侧读它实际随包发布的 appsettings.json，命令一侧只有部署给的环境变量（作业没有自己的 appsettings.json），
    // 所以只写在 API 配置文件里的前缀会在这里暴露为不一致
    [Fact]
    public void The_admin_command_and_the_api_lock_on_the_same_redis_key_prefix()
    {
        var api = ApiLockPrefix(new Dictionary<string, string?>());
        var command = CommandLockPrefix(new Dictionary<string, string?>());

        Assert.False(string.IsNullOrEmpty(api), "The API registers no Redis lock key prefix");
        Assert.Equal(api, command);
    }

    // 部署侧覆盖前缀（例如追加环境）时，两边都取配置值，默认值不会盖过它
    [Fact]
    public void A_configured_redis_key_prefix_overrides_the_default_in_the_api_and_the_admin_command()
    {
        var overrides = new Dictionary<string, string?> { [$"{RedisLockOptions.SectionName}:KeyPrefix"] = "companyname-projectname:staging:" };

        Assert.Equal("companyname-projectname:staging:", ApiLockPrefix(overrides));
        Assert.Equal("companyname-projectname:staging:", CommandLockPrefix(overrides));
    }

    private static string ApiLockPrefix(Dictionary<string, string?> deployment)
    {
        var appSettings = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        Assert.True(File.Exists(appSettings), $"The API appsettings.json was not copied to the test output: {appSettings}");
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(appSettings, optional: false)
            .AddInMemoryCollection(LockConfiguration(deployment))
            .Build();
        // 与宿主一样登记配置：组件按配置节绑定选项时从容器取它
        using var provider = new ServiceCollection().AddLogging().AddSingleton<IConfiguration>(configuration)
            .AddInfrastructureServices(configuration).BuildServiceProvider();
        return provider.GetRequiredService<IOptions<RedisLockOptions>>().Value.KeyPrefix;
    }

    private static string CommandLockPrefix(Dictionary<string, string?> deployment)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(LockConfiguration(deployment)).Build();
        using var provider = AdminCommandServices(configuration).BuildServiceProvider();
        return provider.GetRequiredService<IOptions<RedisLockOptions>>().Value.KeyPrefix;
    }

    // DbMigrator 的管理员命令组合（与 Program 中对应分支相同）
    private static ServiceCollection AdminCommandServices(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddLogging().AddSingleton(configuration);
#if (RemoteTokenAuth)
        services.AddResourceAdminBootstrapServices(configuration);
#else
        services.AddTenantAdminRestoreServices(configuration, new DevelopmentEnvironment());
#endif
        return services;
    }

    // 两边共用的部署配置：同一个 Redis（构建容器不连接它）
    private static Dictionary<string, string?> LockConfiguration(Dictionary<string, string?> deployment)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = "Host=localhost;Database=lock-prefix;Username=postgres",
            ["ConnectionStrings:Redis"] = "localhost:6379,abortConnect=false",
#if (RemoteTokenAuth)
            ["Leistd:ServiceClients:Identity:BaseAddress"] = "https://identity.example",
#endif
        };
        foreach (var (key, value) in deployment) values[key] = value;
        return values;
    }

#endif
    private sealed class DevelopmentEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "migrator-registration";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
