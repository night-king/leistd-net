using CompanyName.ProjectName.DbMigrator.Runners;
#if (RemoteTokenAuth || LocalIdentity)
using CompanyName.ProjectName.Application;
#endif
#if (RemoteTokenAuth)
using CompanyName.ProjectName.Application.Initialization;
using CompanyName.ProjectName.Domain;
#endif
#if (LocalIdentity)
using CompanyName.ProjectName.Domain.Auth.DomainServices;
#endif
using CompanyName.ProjectName.Infrastructure;
#if (RemoteTokenAuth || LocalIdentity)
using CompanyName.ProjectName.Infrastructure.Persistence;
using Leistd.Authorization.EntityFrameworkCore;
#if (IncludeOperationRecords)
using Leistd.OperationRecords.EntityFrameworkCore;
#else
using Leistd.OperationRecords.Logging;
#endif
using Leistd.Security;
using Leistd.Tracing;
#endif
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
#if (RemoteTokenAuth || LocalIdentity)
using Microsoft.Extensions.DependencyInjection.Extensions;
#endif
using Microsoft.Extensions.Hosting;
#if ((RemoteTokenAuth || LocalIdentity) && !IncludeOperationRecords)
using Microsoft.Extensions.Logging;
#endif

namespace CompanyName.ProjectName.DbMigrator;

/// <summary>迁移作业的服务组合。宿主与注册面测试共用这一份，测试校验的就是作业实际解析的组合。</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddMigratorServices(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        // 只注册持久化：迁移目标枚举（ITenantMigrationTargetProvider）随租户连接解析一起注册。
        // 运行期组件依赖只在 API 里注册的当前用户与权限主体，这里用不上
        services.AddPersistenceServices(configuration);
#if (LocalIdentity && IncludeMultiTenancy)
        // 独立库连接串在控制库里加密存储：必须与 API 共享同一密钥环，否则解不开、迁移作业整体停下
        services.AddMyProjectDataProtection(configuration, environment);
#endif
        services.AddScoped<DatabaseMigrationRunner>();
        return services;
    }
#if (RemoteTokenAuth)

    /// <summary>首位管理员引导命令的组合：在持久化之上只加引导要用的运行期能力。</summary>
    /// <remarks>
    /// 授权与操作记录的存储、与 API 启动初始化互斥的锁、用户与角色的领域服务、首次播种用的系统初始化器，以及应用层的权限与动作定义。
    /// 不组合 <c>AddInfrastructureServices</c> 与 <c>AddApplicationServices</c>：应用用例与事件处理器依赖只在 API 里注册的能力
    /// （如业务实时推送的发布器），引导用不上；组合也要能通过构建期校验。
    /// </remarks>
    public static IServiceCollection AddResourceAdminBootstrapServices(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddAmbientContext();
        services.AddCorrelationIdCore();
        services.AddPersistenceServices(configuration);
        services.AddMyProjectCacheAndLock(configuration);
        services.AddPermissionAuthorizationEfCore<MyProjectDbContext>();
#if (IncludeOperationRecords)
        services.AddOperationRecordsEfCore<MyProjectDbContext>();
#else
        services.AddOperationRecordsLogging();
        services.AddLogging(logging => logging.AddFilter("Leistd.OperationRecords", LogLevel.Information));
#endif
        services.AddDomainServices();
        services.AddApplicationDefinitions();
        // 首次建角色与播种走 API 启动初始化的同一实现，二者由同一把初始化锁互斥
        services.TryAddTransient<ISystemInitializer, SystemInitializer>();
        services.AddScoped<ResourceAdminBootstrapRunner>();
        return services;
    }
#endif
#if (LocalIdentity && IncludeMultiTenancy)

    /// <summary>租户管理员恢复命令的组合：在持久化之上只加恢复要用的运行期能力。</summary>
    /// <remarks>
    /// 授权与操作记录的存储、与 API 里租户开通互斥的锁、解开独立库连接串的密钥环，以及应用层的权限与动作定义。
    /// 不组合 <c>AddInfrastructureServices</c> 与 <c>AddApplicationServices</c>：本地身份的完整基础设施带着发信等外部适配器及其启动期校验，
    /// 应用用例依赖它们和只在 API 里注册的能力；部署作业没有那些配置，恢复不能因此被拒，组合也要能通过构建期校验。
    /// </remarks>
    public static IServiceCollection AddTenantAdminRestoreServices(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddAmbientContext();
        services.AddCorrelationIdCore();
        services.AddPersistenceServices(configuration);
        services.AddMyProjectCacheAndLock(configuration);
        // 独立库连接串在控制库里加密存储：与 API 共享同一密钥环，否则解不开、恢复不到该租户的库
        services.AddMyProjectDataProtection(configuration, environment);
        services.AddPermissionAuthorizationEfCore<MyProjectDbContext>();
#if (IncludeOperationRecords)
        services.AddOperationRecordsEfCore<MyProjectDbContext>();
#else
        services.AddOperationRecordsLogging();
        services.AddLogging(logging => logging.AddFilter("Leistd.OperationRecords", LogLevel.Information));
#endif
        services.AddApplicationDefinitions();
        services.AddScoped<TenantAdminRestoreRunner>();
        return services;
    }
#endif
#if (LocalIdentity)

    /// <summary>两步验证部署级重置命令的组合：在持久化之上只加重置要用的运行期能力。</summary>
    /// <remarks>
    /// 与在线写入共用的凭据锁、授权与操作记录的存储（动作与权限定义依赖它们）、结束会话的领域服务；多租户时另加解开独立库连接串的密钥环。
    /// 只注册会话领域服务而不是整组领域服务：两步验证的领域服务要密钥环解密密钥，重置用不上，组合要能通过构建期校验。
    /// 不组合 <c>AddInfrastructureServices</c> 与 <c>AddApplicationServices</c>，理由同租户管理员恢复。
    /// </remarks>
    public static IServiceCollection AddTwoFactorResetServices(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddAmbientContext();
        services.AddCorrelationIdCore();
        services.AddPersistenceServices(configuration);
        services.AddMyProjectCacheAndLock(configuration);
#if (IncludeMultiTenancy)
        services.AddMyProjectDataProtection(configuration, environment);
#endif
        services.AddPermissionAuthorizationEfCore<MyProjectDbContext>();
#if (IncludeOperationRecords)
        services.AddOperationRecordsEfCore<MyProjectDbContext>();
#else
        services.AddOperationRecordsLogging();
        services.AddLogging(logging => logging.AddFilter("Leistd.OperationRecords", LogLevel.Information));
#endif
        services.AddApplicationDefinitions();
        services.TryAddTransient<UserSessionDomainService>();
        services.TryAddTransient<PasskeyDomainService>();
        // 与 API 共享 Redis 时，作业里撤销的会话立即作废 API 的校验缓存；不共享时 API 最多滞后一个校验间隔
        services.AddUserSessionCacheEviction();
        services.AddScoped<TwoFactorResetRunner>();
        return services;
    }
#endif
}
