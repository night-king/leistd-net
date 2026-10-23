using Leistd.Settings.Definitions;
using Leistd.Settings.Events;
using Leistd.Settings.Validation;
using Leistd.OperationRecords.Definitions;
using CompanyName.ProjectName.Application.OperationRecords.EventHandlers;
using CompanyName.ProjectName.Application.Settings.Options;
using CompanyName.ProjectName.Application.Settings.Provider;
using CompanyName.ProjectName.Application.Settings.Validators;
using Microsoft.Extensions.Options;
#if (Email)
using CompanyName.ProjectName.Application.Settings.AppServices;
#endif
#if (LocalIdentity)
using CompanyName.ProjectName.Application.Auth.Policies;
using CompanyName.ProjectName.Application.Users.Validators;
#endif
using CompanyName.ProjectName.Application.Settings.Timing;
#if (LocalIdentity)
using CompanyName.ProjectName.Application.Auth.AppServices;
using CompanyName.ProjectName.Application.Auth.BackgroundJobs;
using CompanyName.ProjectName.Application.Auth.Captcha;
#if (Email)
using CompanyName.ProjectName.Application.Auth.EmailVerification;
#endif
using CompanyName.ProjectName.Application.Auth.SecurityAlerts;
using CompanyName.ProjectName.Application.Auth.EventHandlers;
using CompanyName.ProjectName.Application.Auth.Events;
using CompanyName.ProjectName.Application.Auth.Sessions;
using CompanyName.ProjectName.Application.Auth.SignIn;
using CompanyName.ProjectName.Application.Auth.TwoFactor;
using CompanyName.ProjectName.Domain.Auth.Events;
using Leistd.BackgroundJobs;
using Leistd.BackgroundJobs.Recurring;
#if (OpenIddictServer)
using CompanyName.ProjectName.Application.OpenApplications.AppServices;
using CompanyName.ProjectName.Application.Auth.Options;
#endif
#endif
using CompanyName.ProjectName.Application.Initialization;
using CompanyName.ProjectName.Application.Statistics.AppServices;
using CompanyName.ProjectName.Application.Users.AppServices;
using Leistd.ObjectMapping.Mapster;
using Mapster;
using Leistd.Authorization;
using Leistd.Authorization.Events;
using CompanyName.ProjectName.Application.OperationRecords.Provider;
using CompanyName.ProjectName.Application.Permissions.Provider;
using CompanyName.ProjectName.Application.Roles.AppServices;
#if (IncludeRealTime)
using CompanyName.ProjectName.Application.RealTime.Provider;
using CompanyName.ProjectName.Application.Roles.EventHandlers;
using CompanyName.ProjectName.Application.Roles.Events;
using Leistd.RealTime.Subscriptions;
#endif
#if (LocalIdentity && IncludeMultiTenancy)
using CompanyName.ProjectName.Application.Tenants.Provider;
#if (Impersonation)
using CompanyName.ProjectName.Application.Tenants.AppServices;
#endif
using Leistd.MultiTenancy.Management;
using Leistd.MultiTenancy.Management.Events;
using Leistd.MultiTenancy.Management.Provisioning;
#endif
using Leistd.EventBus.EventHandlers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Leistd.Authorization.Definitions;
using Leistd.Authorization.Subjects;

namespace CompanyName.ProjectName.Application;

public static class DependencyInjection
{
    private static readonly Action<TypeAdapterConfig> ScanMappings =
        config => config.Scan(typeof(DependencyInjection).Assembly);

    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddMapsterObjectMapper(options =>
        {
            // 各模块 Mappings/ 下实现 IRegister 的映射配置，登记到组件自己的 TypeAdapterConfig；
            // 重复调用本入口时同一扫描只登记一次
            if (!options.Configurators.Contains(ScanMappings))
                options.Configurators.Add(ScanMappings);
        });

        services.TryAddTransient<ISystemInitializer, SystemInitializer>();

#if (LocalIdentity)
        services.TryAddTransient<UserSortingValidator>();
        services.TryAddTransient<ICaptchaVerifier, CaptchaVerifier>();
        services.TryAddTransient<ICaptchaAppService, CaptchaAppService>();
#if (Email)
        services.TryAddTransient<EmailChallengeStore>();
        services.TryAddTransient<PasswordResetCodeMailer>();
        services.TryAddTransient<TwoFactorCodeMailComposer>();
        services.TryAddTransient<IEmailVerificationAppService, EmailVerificationAppService>();
        services.TryAddTransient<IPasswordResetAppService, PasswordResetAppService>();
#endif
        services.TryAddTransient<SessionIssuer>();
        services.TryAddTransient<IUserSessionValidator, UserSessionValidator>();
        services.TryAddTransient<ISessionIdleTimeoutReader, SessionIdleTimeoutReader>();
        // 本次请求里已校验的会话与它是否受限：认证事件写入，中间件、协议端点与"当前用户"同读这一份
        services.TryAddScoped<ICurrentSessionState, CurrentSessionState>();
        services.AddUserSessionCacheEviction();
        services.TryAddTransient<CurrentSessionTerminator>();
        services.TryAddTransient<UserAccessRevoker>();
        services.TryAddTransient<IUserSessionAppService, UserSessionAppService>();
        // 安全提醒默认不发；启用通知时宿主换成经通知组件发布的实现
        services.TryAddTransient<ISecurityAlertPublisher, NullSecurityAlertPublisher>();
        // 改密、重置与两步验证变更的提醒，以及设置密钥的清理，都在提交之后执行
        services.TryAddEnumerable(ServiceDescriptor.Transient<IEventHandler<SecurityAlertRequestedEvent>, SecurityAlertRequestedEventHandler>());
        // 换邮箱的提醒：旧的已验证地址直接收一封账号安全邮件（启用邮件时），另发一条普通安全提醒
        services.TryAddEnumerable(ServiceDescriptor.Transient<IEventHandler<EmailChangedEvent>, EmailChangedEventHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Transient<IEventHandler<TwoFactorSetupCompletedEvent>, TwoFactorSetupCompletedEventHandler>());
        services.TryAddTransient<TwoFactorChallengeStore>();
        services.TryAddTransient<TwoFactorMethodResolver>();
        // 不再登录的用户没有"登录时顺手清理"的时机，过期会话与其中的原始 IP 由这个作业每天清掉
        services.AddRecurringJob<ExpiredUserSessionCleanupJob>(
            ExpiredUserSessionCleanupJob.Name,
            RecurringJobSchedule.DailyAt(new TimeOnly(3, 0)),
            RecurringJobScope.Cluster);
        services.TryAddTransient<ITwoFactorAppService, TwoFactorAppService>();
        services.TryAddTransient<IAuthAppService, AuthAppService>();

#if (OpenIddictServer)
        // 验证器与变更令牌源共同标识已绑定；重复绑定会追加资源数组。
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(IOptionsChangeTokenSource<OAuthResourceOptions>)) ||
            !services.Any(descriptor => descriptor.ServiceType == typeof(IValidateOptions<OAuthResourceOptions>) &&
                descriptor.ImplementationType == typeof(OAuthResourceOptionsValidator)))
        {
            services.AddOptions<OAuthResourceOptions>()
                .BindConfiguration(OAuthResourceOptions.SectionName)
                .ValidateOnStart();
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<OAuthResourceOptions>, OAuthResourceOptionsValidator>());
        }
        // OAuth 主体工厂与开放应用管理仅供自签发令牌模式使用。
        services.TryAddTransient<IAuthPrincipalFactory, AuthPrincipalFactory>();
        services.TryAddTransient<IOpenApplicationAppService, OpenApplicationAppService>();
#endif
#if (ExternalLogin)
        services.TryAddTransient<IExternalAuthAppService, ExternalAuthAppService>();
#endif
#endif

        services.TryAddTransient<IUserAppService, UserAppService>();

        services.TryAddTransient<IRoleAppService, RoleAppService>();

        services.TryAddTransient<IPlatformStatisticsAppService, PlatformStatisticsAppService>();

        services.AddApplicationDefinitions();
        // 语言与时区的部署基线是这两项设置的代码默认值；所有形态都有，绑定在定义提供器旁边。
        // 以校验器是否已登记判断绑定过没有：重复绑定会追加一份变更令牌源，注册就不再幂等
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(IValidateOptions<DisplayDefaultsOptions>) &&
                descriptor.ImplementationType == typeof(DisplayDefaultsOptionsValidator)))
        {
            services.AddOptions<DisplayDefaultsOptions>()
                .BindConfiguration(DisplayDefaultsOptions.SectionName)
                .ValidateOnStart();
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<DisplayDefaultsOptions>, DisplayDefaultsOptionsValidator>());
        }
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISettingDefinitionProvider, SettingDefinitionProvider>());
        // 设置值的业务校验：值域（布尔、区间、候选）写在定义上，这里只放定义表达不了的规则
        services.TryAddEnumerable(ServiceDescriptor.Transient<ISettingValueValidator, TimeZoneSettingValidator>());
#if (IncludeNotifications)
        services.TryAddEnumerable(ServiceDescriptor.Transient<ISettingValueValidator, NotificationRetentionSettingValidator>());
#endif
#if (Email)
        services.TryAddEnumerable(ServiceDescriptor.Transient<ISettingValueValidator, EmailSettingValidator>());
        services.TryAddTransient<IEmailSettingsAppService, EmailSettingsAppService>();
#endif
        // 组件写入后发布的事件转成本项目的操作记录（提交后分发，回滚的写入不留痕）
        services.TryAddEnumerable(ServiceDescriptor.Transient<IEventHandler<SettingChangedEvent>, SettingChangedAuditHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Transient<IEventHandler<PermissionGrantsReplacedEvent>, PermissionGrantsReplacedAuditHandler>());
#if (IncludeRealTime)
        // 业务实时：角色列表变化后推给订阅者（有工作单元时在提交之后）
        services.TryAddEnumerable(ServiceDescriptor.Transient<IEventHandler<RoleListChangedEvent>, RoleListChangedEventHandler>());
        // 订阅授权必须由本项目明确选择（框架不给默认实现）：资源键须属于当前租户或宿主作用域，
        // 且订阅者持有查看该资源的权限
        services.TryAddTransient<IRealTimeSubscriptionAuthorizer, AppRealTimeSubscriptionAuthorizer>();
#endif
        // 服务端产出给人看的时间文本时注入它；DTO 保持 UTC 交给前端渲染，不必经过这里。
        services.TryAddTransient<IUserTimeZoneProvider, UserTimeZoneProvider>();
#if (LocalIdentity)
        // 注册策略按租户从设置里解析；appsettings 仍是部署基线（设置定义的默认值取自它）。
        services.TryAddTransient<IUserRegistrationPolicyProvider, UserRegistrationPolicyProvider>();
        services.TryAddTransient<ILoginSecurityPolicyProvider, LoginSecurityPolicyProvider>();
        // 口令登录、两步验证登录、再认证三条路径共用同一份失败计数与锁定
        services.TryAddTransient<IAccessFailureCounter, AccessFailureCounter>();
        services.TryAddTransient<IReauthenticationGuard, ReauthenticationGuard>();
#endif

#if (LocalIdentity && IncludeMultiTenancy)
        // 租户管理的编排与补偿在多租户组件里（存储由 Infrastructure 的 AddMultiTenancyEfCore 提供）；
        // 本项目只负责开通内容与启用前置条件
        services.AddTenantManagement();
        services.TryAddTransient<ITenantProvisioner, TenantSeeder>();
        services.TryAddEnumerable(ServiceDescriptor.Transient<ITenantActivationGuard, TenantHasUsersActivationGuard>());
        services.TryAddEnumerable(ServiceDescriptor.Transient<IEventHandler<TenantChangedEvent>, TenantChangedAuditHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Transient<IEventHandler<TenantConnectionChangedEvent>, TenantConnectionChangedAuditHandler>());
#if (Impersonation)
        services.TryAddTransient<ITenantImpersonationAppService, TenantImpersonationAppService>();
#endif
#endif

        return services;
    }

#if (LocalIdentity)
    /// <summary>会话撤销后作废它的校验缓存（事务提交后由本地事件总线分发），撤销对已发出的 Cookie 立即生效。</summary>
    /// <remarks>部署作业撤销会话时也注册它：与 API 共享 Redis 时，作业里的撤销同样立即作废 API 的校验缓存。</remarks>
    public static IServiceCollection AddUserSessionCacheEviction(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Transient<IEventHandler<UserSessionRevokedEvent>, UserSessionRevokedEventHandler>());
        return services;
    }

#endif
    /// <summary>注册本项目的权限定义与操作动作定义，以及权限组件解析主体所需的本项目实现。</summary>
    /// <remarks>
    /// <see cref="AddApplicationServices"/> 已包含它。只组合部分运行期能力的一次性作业（DbMigrator 的管理员引导与恢复）单独调用：
    /// 它们要按定义播种或补授权限、记录本项目登记的动作，而不需要登录、发信、实时推送等用例。
    /// </remarks>
    public static IServiceCollection AddApplicationDefinitions(this IServiceCollection services)
    {
        services.AddPermissionAuthorizationCore();
        // Scoped：一次请求内的主体解析结果被 PermissionSubjectProvider 与 IPermissionChecker 共享。
        services.TryAddScoped<IPermissionSubjectProvider, PermissionSubjectProvider>();
        // 权限管理用例（端点由 Api 映射）经它确认主体存在、取显示名
        services.TryAddTransient<IPermissionSubjectDirectory, PermissionSubjectDirectory>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPermissionDefinitionProvider, PermissionDefinitionProvider>());
        // 动作定义与权限、设置同属"启动期一次性登记"的定义族，注册方式与它们一致。
        // 不注册的话管理器拿到空索引：界面按"未登记码"降级为原样显示裸码，
        // 症状是页面照常能用、只是动作列全是机器码——不会报错，所以很容易漏。
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IOperationActionDefinitionProvider, OperationActionDefinitionProvider>());
        return services;
    }
}
