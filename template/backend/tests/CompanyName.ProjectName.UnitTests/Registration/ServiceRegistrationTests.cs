#if (!RemoteTokenAuth)
using Leistd.Settings.Validation;
#endif
using CompanyName.ProjectName.Api;
using CompanyName.ProjectName.Api.Auth.Authorization;
#if (LocalIdentity)
using CompanyName.ProjectName.Api.Auth.Authentication;
using Leistd.Security.AspNetCore.RequestContext;
#endif
using CompanyName.ProjectName.Api.Hosting;
#if (IncludeNotifications && LocalIdentity)
using CompanyName.ProjectName.Api.Notifications;
using CompanyName.ProjectName.Application.Auth.SecurityAlerts;
#endif
#if (IncludeNotifications && Email)
using CompanyName.ProjectName.Application.Notifications.Provider;
using Leistd.Notifications.Email.Recipients;
#endif
using CompanyName.ProjectName.Application;
using CompanyName.ProjectName.Application.Settings.Validators;
#if (OpenIddictServer)
using CompanyName.ProjectName.Application.Auth.Options;
#endif
#if (LocalIdentity)
using CompanyName.ProjectName.Application.Auth.Sessions;
using Leistd.Security.RequestContext;
#endif
#if (IncludeRealTime)
using CompanyName.ProjectName.Application.RealTime.Provider;
#endif
using CompanyName.ProjectName.Domain;
using CompanyName.ProjectName.Domain.Users.DomainServices;
using CompanyName.ProjectName.Infrastructure;
using CompanyName.ProjectName.Infrastructure.Persistence;
#if (LocalIdentity && IncludeMultiTenancy)
using CompanyName.ProjectName.Infrastructure.TenantConnections.Provider;
using Leistd.MultiTenancy.Management.Provisioning;
#endif
using Leistd.Lock.Abstractions;
using Leistd.ObjectMapping.Abstractions;
using Leistd.ObjectMapping.Mapster.Options;
#if (IncludeRealTime)
using Leistd.RealTime.Subscriptions;
#endif
using Microsoft.AspNetCore.Authorization;
#if (LocalIdentity)
using Microsoft.AspNetCore.Builder;
#endif
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace CompanyName.ProjectName.UnitTests.Registration;

/// <summary>
/// 注册面：各层入口 <c>AddDomainServices()</c>、<c>AddApplicationServices()</c>、
/// <c>AddInfrastructureServices()</c>/<c>AddPersistenceServices()</c>、<c>AddApiAuthorization()</c> 与宿主扩展，
/// 以及与组件入口的组合顺序。
/// </summary>
/// <remarks>
/// 注册结果是组合根的契约，编译期完全看不出来：生命周期写错、重复注册、
/// 后注册意外覆盖先注册，全都要到运行时才发作。
/// 在 <see cref="IServiceCollection"/> 上断言不需要宿主，成本是集成测试的百分之一。
/// </remarks>
public class ServiceRegistrationTests
{
    [Fact]
    public void Domain_services_are_registered_as_transient()
    {
        var services = new ServiceCollection();

        services.AddDomainServices();

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(UserDomainService));
        Assert.Equal(ServiceLifetime.Transient, descriptor.Lifetime);
        var roles = Assert.Single(services, d => d.ServiceType == typeof(RoleDomainService));
        Assert.Equal(ServiceLifetime.Transient, roles.Lifetime);
    }

    // 组合根拆分后重复调用是常态。不幂等会让同一实现出现多条，
    // 按 IEnumerable 解析时重复执行，单例被建两份。
    [Fact]
    public void Domain_registration_is_idempotent()
    {
        var services = new ServiceCollection();

        services.AddDomainServices();
        var afterFirst = services.Count;
        services.AddDomainServices();

        Assert.Equal(afterFirst, services.Count);
    }

    // 值域之外的业务校验漏登记不会报错，只会让非法值静默落库，之后每个消费方都得自己防御。
    // 账户偏好的取值判定在每种形态都有（资源服务转交前先判）；时区设置只在持有偏好的服务里定义。
    [Fact]
    public void Value_validators_are_registered_in_every_service_role()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddApplicationServices();

        Assert.Contains(services, d => d.ServiceType == typeof(AccountPreferenceValueValidator));
#if (!RemoteTokenAuth)
        Assert.Contains(services, d => d.ServiceType == typeof(ISettingValueValidator) && d.ImplementationType == typeof(TimeZoneSettingValidator));
#endif
    }

    [Fact]
    public void Application_registration_brings_in_the_object_mapper()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddApplicationServices();

        Assert.Contains(services, d => d.ServiceType == typeof(IObjectMapper));
    }

    // 映射配置经程序集扫描发现，扫描在构建映射器时执行：不需要宿主也能解析出可用的映射器。
    [Fact]
    public void Application_services_resolve_without_a_host()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplicationServices();

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IObjectMapper>());
    }

    // 重复调用不能让多实现扩展点（事件处理器、设置校验器、定义提供方）多出一份——
    // 那会让每个事件处理两次、每条定义登记两次；映射扫描也只登记一次。
    // 组件入口内部的 Options 配置回调可能随调用累加，因此按实际效果比较，而不是比较描述符总数。
    [Fact]
    public void Application_registration_is_idempotent()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
#if (OpenIddictServer)
            ["OAuth:Resource"] = "cohesion-api",
            ["OAuth:ApiResources:0:Name"] = "downstream-api",
            ["OAuth:ApiResources:0:Scope"] = "downstream.read",
            ["OAuth:ApiResources:0:OwnerClientId"] = "caller-app"
#endif
        }).Build();
        var once = new ServiceCollection().AddLogging().AddSingleton<IConfiguration>(configuration).AddApplicationServices();
        var twice = new ServiceCollection().AddLogging().AddSingleton<IConfiguration>(configuration).AddApplicationServices().AddApplicationServices();

        Assert.Equal(Registrations(once), Registrations(twice));
        using var onceProvider = once.BuildServiceProvider();
        using var twiceProvider = twice.BuildServiceProvider();
        Assert.Equal(
            onceProvider.GetRequiredService<IOptions<MapsterOptions>>().Value.Configurators.Count,
            twiceProvider.GetRequiredService<IOptions<MapsterOptions>>().Value.Configurators.Count);
#if (OpenIddictServer)
        var onceOptions = onceProvider.GetRequiredService<IOptions<OAuthResourceOptions>>().Value;
        var twiceOptions = twiceProvider.GetRequiredService<IOptions<OAuthResourceOptions>>().Value;
        Assert.Equal("cohesion-api", onceOptions.Resource);
        Assert.Equal(onceOptions.Resource, twiceOptions.Resource);
        var onceResource = Assert.Single(onceOptions.ApiResources);
        var twiceResource = Assert.Single(twiceOptions.ApiResources);
        Assert.Equal(("downstream-api", "downstream.read", "caller-app"), (onceResource.Name, onceResource.Scope, onceResource.OwnerClientId));
        Assert.Equal((onceResource.Name, onceResource.Scope, onceResource.OwnerClientId), (twiceResource.Name, twiceResource.Scope, twiceResource.OwnerClientId));
#endif
    }
#if (OpenIddictServer)

    [Fact]
    public void Application_registration_binds_configuration_with_a_host_registered_validator()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OAuth:Resource"] = "host-api"
        }).Build();
        var services = new ServiceCollection().AddLogging().AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IValidateOptions<OAuthResourceOptions>, OAuthResourceOptionsValidator>();
        services.AddApplicationServices().AddApplicationServices();
        using var provider = services.BuildServiceProvider();

        Assert.Equal("host-api", provider.GetRequiredService<IOptions<OAuthResourceOptions>>().Value.Resource);
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IOptionsChangeTokenSource<OAuthResourceOptions>));
        Assert.Single(provider.GetServices<IValidateOptions<OAuthResourceOptions>>());
    }
#endif
#if (IncludeRealTime)

    // 实时 Hub 映射时要求订阅授权器已登记（框架不给默认实现）：它属于应用层的授权规则，随应用层入口登记
    [Fact]
    public void Application_registration_chooses_the_realtime_subscription_authorizer()
    {
        var services = new ServiceCollection().AddLogging().AddApplicationServices();

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(IRealTimeSubscriptionAuthorizer));
        Assert.Equal(typeof(AppRealTimeSubscriptionAuthorizer), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Transient, descriptor.Lifetime);
    }
#endif
#if (LocalIdentity && IncludeMultiTenancy)

    // 组件（AddTenantManagement）按 TryAdd 挂"不翻译"的默认实现，本项目有意覆盖：
    // 不论哪一层先注册，生效的都是 PostgreSQL 方言的翻译，且只有一条
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_postgres_tenant_error_describer_wins_in_either_registration_order(bool infrastructureFirst)
    {
        var configuration = Configuration(redis: false);
        var services = new ServiceCollection().AddLogging();

        if (infrastructureFirst)
        {
            services.AddInfrastructureServices(configuration);
            services.AddApplicationServices();
        }
        else
        {
            services.AddApplicationServices();
            services.AddInfrastructureServices(configuration);
        }

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(ITenantDatabaseErrorDescriber));
        Assert.Equal(typeof(PostgresTenantDatabaseErrorDescriber), descriptor.ImplementationType);
    }
#endif
#if (IncludeNotifications && LocalIdentity)

    // 应用层默认不发安全提醒（TryAdd），启用通知时宿主有意替换为经通知组件发布：与调用先后无关
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Notification_security_alerts_replace_the_silent_default_in_either_order(bool applicationFirst)
    {
        var services = new ServiceCollection().AddLogging();

        if (applicationFirst)
        {
            services.AddApplicationServices();
            services.AddMyProjectNotifications();
        }
        else
        {
            services.AddMyProjectNotifications();
            services.AddApplicationServices();
        }

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(ISecurityAlertPublisher));
        Assert.Equal(typeof(NotificationSecurityAlertPublisher), descriptor.ImplementationType);
    }
#endif

    // 基础设施入口按 ConnectionStrings:Redis 选择分布式缓存与锁的实现（未配置时退回进程内实现）；
    // 两种配置下重复调用都不多出登记，业务上下文按作用域登记
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Infrastructure_registration_selects_cache_and_lock_by_configuration_and_is_idempotent(bool redis)
    {
        var configuration = Configuration(redis);
        var once = new ServiceCollection().AddLogging().AddInfrastructureServices(configuration);
        var twice = new ServiceCollection().AddLogging()
            .AddInfrastructureServices(configuration).AddInfrastructureServices(configuration);

        Assert.Equal(Registrations(once), Registrations(twice));
        var cache = Assert.Single(once, d => d.ServiceType == typeof(IDistributedCache));
        Assert.Equal(ServiceLifetime.Singleton, cache.Lifetime);
        Assert.Equal(redis, cache.ImplementationType != typeof(MemoryDistributedCache));
        Assert.Equal(ServiceLifetime.Singleton, Assert.Single(once, d => d.ServiceType == typeof(IDistributedLock)).Lifetime);
        Assert.Equal(redis, once.Any(d => d.ServiceType == typeof(IConnectionMultiplexer)));
        Assert.Equal(ServiceLifetime.Scoped, Assert.Single(once, d => d.ServiceType == typeof(MyProjectDbContext)).Lifetime);
    }

    // 迁移作业只调用持久化入口：业务上下文按作用域登记一次，重复调用不多出登记，也不带入缓存与锁
    [Fact]
    public void Persistence_registration_registers_the_business_context_once_without_runtime_components()
    {
        var configuration = Configuration(redis: true);
        var once = new ServiceCollection().AddLogging().AddPersistenceServices(configuration);
        var twice = new ServiceCollection().AddLogging()
            .AddPersistenceServices(configuration).AddPersistenceServices(configuration);

        Assert.Equal(Registrations(once), Registrations(twice));
        Assert.Equal(ServiceLifetime.Scoped, Assert.Single(once, d => d.ServiceType == typeof(MyProjectDbContext)).Lifetime);
        Assert.DoesNotContain(once, d => d.ServiceType == typeof(IDistributedCache));
        Assert.DoesNotContain(once, d => d.ServiceType == typeof(IConnectionMultiplexer));
    }

    // 健康检查按名称登记，同名再登记一次会让 HealthCheckService 解析失败；宿主入口重复调用必须仍能解析
    [Fact]
    public void Web_host_registration_can_be_called_twice_and_still_resolves_health_checks()
    {
        var once = new ServiceCollection().AddLogging().AddMyProjectWebHost();
        var twice = new ServiceCollection().AddLogging().AddMyProjectWebHost().AddMyProjectWebHost();

        Assert.Equal(Registrations(once), Registrations(twice));
        using var provider = twice.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<HealthCheckService>());
        var names = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations
            .Select(registration => registration.Name)
            .ToList();
        Assert.Equal(names.Distinct().Count(), names.Count);
        Assert.Contains("self", names);
    }
#if (LocalIdentity)

    // 请求来源信息只读当前 HttpContext、不持有状态：按无状态服务登记为 Transient
    [Fact]
    public void Request_client_info_is_registered_as_transient()
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());

        builder.AddLocalSessionAuthentication();

        var descriptor = Assert.Single(builder.Services, d => d.ServiceType == typeof(IRequestClientInfo));
        Assert.Equal(typeof(HttpRequestClientInfo), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Transient, descriptor.Lifetime);
    }

    // 会话状态由认证事件写入、中间件与应用服务读取：同一请求（作用域）内必须是同一个实例，请求之间互不相干
    [Fact]
    public void Current_session_state_is_shared_within_a_request_and_isolated_between_requests()
    {
        var services = new ServiceCollection().AddLogging().AddApplicationServices();
        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(ICurrentSessionState));
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var sessionId = Guid.NewGuid();
        using (var request = provider.CreateScope())
        {
            request.ServiceProvider.GetRequiredService<ICurrentSessionState>().Set(sessionId, twoFactorSetupRequired: true);
            var later = request.ServiceProvider.GetRequiredService<ICurrentSessionState>();
            Assert.Equal(sessionId, later.SessionId);
            Assert.True(later.TwoFactorSetupRequired);
        }

        using var next = provider.CreateScope();
        var fresh = next.ServiceProvider.GetRequiredService<ICurrentSessionState>();
        Assert.Null(fresh.SessionId);
        Assert.False(fresh.TwoFactorSetupRequired);
    }

    // 受限状态的判定在 Application，HTTP 桥接在 Api：Application 不引用 ASP.NET Core 的 HTTP 抽象（HttpContext 等）
    [Fact]
    public void The_application_layer_does_not_reference_http_abstractions()
    {
        var referenced = typeof(ICurrentSessionState).Assembly.GetReferencedAssemblies().Select(name => name.Name!).ToList();

        Assert.DoesNotContain(referenced, name => name.StartsWith("Microsoft.AspNetCore.Http", StringComparison.Ordinal));
        Assert.DoesNotContain(referenced, name => name == "Microsoft.AspNetCore.Mvc.Core");
    }
#endif
#if (IncludeNotifications && Email)

    // 邮件收件人解析无状态（每次按仓储查询）：按无状态服务登记为 Transient
    [Fact]
    public void Notification_recipient_resolver_is_registered_as_transient()
    {
        var services = new ServiceCollection().AddLogging().AddMyProjectNotifications();

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(INotificationRecipientResolver));
        Assert.Equal(typeof(UserEmailRecipientResolver), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Transient, descriptor.Lifetime);
    }
#endif

    // ASP.NET Core 只认一个授权结果处理器：本项目的处理器替换官方默认实现，与 AddAuthorization 的先后无关，重复调用也只有一条
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Api_authorization_replaces_the_default_result_handler_in_either_order(bool apiFirst)
    {
        var services = new ServiceCollection();

        if (apiFirst)
        {
            services.AddApiAuthorization();
            services.AddAuthorization();
        }
        else
        {
            services.AddAuthorization();
            services.AddApiAuthorization();
        }
        services.AddApiAuthorization();

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(IAuthorizationMiddlewareResultHandler));
        Assert.Equal(typeof(ApiAuthorizationResultHandler), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
#if (RemoteTokenAuth)

        // 成员状态判定只登记一次：重复登记会让每次授权判两次、Hub 调用里查两次库
        var snapshot = Assert.Single(services, d => d.ServiceType == typeof(LocalMemberAccessSnapshot));
        Assert.Equal(ServiceLifetime.Scoped, snapshot.Lifetime);
        var handler = Assert.Single(services, d => d.ImplementationType == typeof(LocalMemberAccessHandler));
        Assert.Equal(typeof(IAuthorizationHandler), handler.ServiceType);
        Assert.Equal(ServiceLifetime.Scoped, handler.Lifetime);
#endif
    }

    private static IConfiguration Configuration(bool redis) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(redis
                ? new Dictionary<string, string?> { ["ConnectionStrings:Redis"] = "localhost:6379" }
                : new Dictionary<string, string?>())
            .Build();

    // 服务类型、实现与生命周期的多重集合；Options 配置回调按实际效果另行比较
    private static List<string> Registrations(IServiceCollection services) =>
        services
            .Where(d => !IsOptionsCallback(d.ServiceType))
            .Select(d => $"{d.ServiceType.FullName}|{d.Lifetime}|" +
                (d.ImplementationType?.FullName ?? d.ImplementationInstance?.GetType().FullName ?? "factory"))
            .Order(StringComparer.Ordinal)
            .ToList();

    private static bool IsOptionsCallback(Type serviceType) =>
        serviceType.IsGenericType &&
        serviceType.GetGenericTypeDefinition() is var definition &&
        (definition == typeof(IConfigureOptions<>) || definition == typeof(IPostConfigureOptions<>));
}
