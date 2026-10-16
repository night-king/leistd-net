using CompanyName.ProjectName.Api;
using CompanyName.ProjectName.Api.Auth;
#if (OpenIddictServer)
using CompanyName.ProjectName.Api.Auth.OpenIddict;
#endif
using CompanyName.ProjectName.Api.Configuration;
using CompanyName.ProjectName.Api.Hosting;
#if (IncludeLocalization)
using CompanyName.ProjectName.Api.Localization;
#endif
using CompanyName.ProjectName.Api.Middlewares;
using CompanyName.ProjectName.Api.Options;
using CompanyName.ProjectName.Application;
#if (LocalIdentity && IncludeMultiTenancy)
using CompanyName.ProjectName.Application.Shared;
#endif
#if (IncludeLocalization)
using CompanyName.ProjectName.Application.Settings.Provider;
#endif
using CompanyName.ProjectName.Domain;
using CompanyName.ProjectName.Infrastructure;
#if (!SpaFrontend && (IncludeNotifications || IncludeRealTime))
using Leistd.AspNetCore.SignalR;
#endif
using Leistd.Authorization.AspNetCore;
#if (IncludeLocalization)
using Leistd.Authorization.Options;
#endif
using Leistd.BackgroundJobs.InProcess;
using Leistd.DependencyInjection.DynamicProxy.Registration;
using Leistd.ExceptionHandling.AspNetCore;
#if (IncludeLocalization)
using Leistd.Localization.AspNetCore;
#endif
#if (!IncludeMultiTenancy)
using Leistd.MultiTenancy;
#endif
#if (IncludeMultiTenancy)
using Leistd.MultiTenancy.AspNetCore;
#endif
using Leistd.MultiTenancy.Context;
#if (IncludeNotifications && !IncludeRealTime)
using Leistd.Notifications.AspNetCore.SignalR;
#endif
#if (!IncludeOperationRecords)
using Leistd.OperationRecords.Logging;
#endif
#if (IncludeRealTime)
using Leistd.RealTime.AspNetCore.SignalR;
#endif
using Leistd.OperationRecords.AspNetCore;
using Leistd.Security.AspNetCore;
using Leistd.Settings.Hosting;
#if (IncludeLocalization)
using Leistd.Settings.Options;
#endif
using Leistd.Tracing.AspNetCore;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateLogger();

try
{
    Log.Information("Starting web application ...");
    var builder = WebApplication.CreateBuilder(args);

    // 生产以外都校验作用域与构建期依赖：开发、集成测试宿主（Testing）、预发在启动时就暴露注册错误，
    // 只有生产为启动耗时省掉这一步
    var validateServiceProvider = !builder.Environment.IsProduction();
    builder.Host.UseServiceProviderFactory(new DynamicProxyServiceRegistrationCallbackFactory(
        new ServiceProviderOptions
        {
            ValidateScopes = validateServiceProvider,
            ValidateOnBuild = validateServiceProvider
        }));

    // 宿主级设置作为优先级最高的配置源（日志级别、发信参数、操作记录保留期，见 HostSettingBindings）：
    // 设置里有值的项覆盖部署配置，消费方照常注入 IOptionsMonitor<T>。写入后本进程立即应用，
    // 其它实例由周期任务跟上（Leistd:Settings:Hosting:RefreshInterval）。配置源在 Build 之后挂上（UseHostSettings）
    builder.Services.AddMyProjectHostSettings();

    // 请求体沿用 Kestrel 默认上限（约 30 MB）：需要更大上传的端点用 [RequestSizeLimit] / [RequestFormLimits] 单独放宽
    builder.AddMyProjectLogging();

    builder.Services.AddDomainServices();
    builder.Services.AddInfrastructureServices(builder.Configuration);
    builder.Services.AddApplicationServices();
#if (!IncludeOperationRecords)
    builder.Services.AddOperationRecordsLogging();
#endif

#if (OpenIddictServer)
    builder.AddMyProjectOpenIddictServer();
#endif
    builder.Services.AddMyProjectWebHost();

    // 周期任务调度与进程内队列：组件登记的维护任务（操作记录归档、通知保留期、宿主级设置刷新）由它执行。
    // 集群任务经分布式锁 + 完成水位保证多副本同一时段只跑一次（多副本部署须配置 Redis）
    builder.Services.AddInProcessBackgroundJobs();

    builder.Services.AddGlobalExceptionHandler(ApiExceptionMappings.Configure);
#if (IncludeLocalization)
    builder.Services.AddJsonLocalization(options =>
    {
        options.SupportedCultures = [.. SettingConstant.Display.SupportedLanguages];
        options.ResourceAssemblies.Add(typeof(Program).Assembly);
        // 只有显式登记的强类型资源才路由到 JSON。
        options.JsonResourceTypes.Add(typeof(ApiResource));
    });
    // 设置页与权限树的显示名按 Setting:{名称}、SettingGroup:{分组} 与权限定义的显示名键查 ApiResource
    builder.Services.Configure<SettingManagementOptions>(options => options.LocalizationResource = typeof(ApiResource));
    builder.Services.Configure<PermissionManagementOptions>(options => options.LocalizationResource = typeof(ApiResource));
#endif
    builder.Services.AddSecurity();

#if (IncludeMultiTenancy)
#if (LocalIdentity)
    // Identity 持有租户注册表，因此校验解析结果。
    builder.Services.AddMultiTenancy();
#else
    // Resource 不持有注册表，只信已验证令牌的 tenant_id claim。
    builder.Services.AddMultiTenancy(options => options.ValidateResolvedTenant = false);
#endif
#else
    builder.Services.AddMultiTenancyCore();
#endif

#if (IncludeNotifications || IncludeRealTime)
    // 心跳、超时、详细错误是 SignalR 自身的选项，由宿主直接配置。
    // 配了 Redis 时接上跨副本背板，多副本与 Worker 的推送经它路由
    builder.Services.AddSignalR(options =>
    {
        options.EnableDetailedErrors = builder.Environment.IsDevelopment();
    }).AddMyProjectBackplane(builder.Configuration);
#endif
#if (IncludeRealTime)

    // 业务实时：资源事件推给订阅者，订阅授权由 AddApplicationServices 登记
    builder.Services.AddRealTimeSignalR();
#endif
#if (IncludeNotifications)
    builder.Services.AddMyProjectNotifications();
#endif

    builder.AddMyProjectAuthentication();
    builder.Services.AddApiAuthorization();
    // 将权限定义作为 ASP.NET Core 授权策略解析。
    builder.Services.AddPermissionAuthorization();

    var app = builder.Build();
    // 宿主级设置配置源要在构建之后挂上：构建期间追加的配置源（如测试宿主的覆盖）不能排在它后面
    app.UseHostSettings();

#if (SpaFrontend)
    // 缺 Redis 不阻止启动：单实例配 DataProtection:KeysPath 是合法部署，副本数又无从推断。
    // 但分布式缓存随之回落到进程内存，多副本时各副本各一份、彼此看不见，只能靠这条告警在启动日志里发现。
    // 分布式锁的回落由锁组件自己告警，这里不重复。
    if (!app.Environment.IsDevelopment() && string.IsNullOrEmpty(app.Configuration.GetConnectionString("Redis")))
    {
#if (Email)
        const string cacheBackedState = "server-side session tickets, session revocation checks, two-factor challenges, captchas and email verification codes";
#elif (LocalIdentity)
        const string cacheBackedState = "server-side session tickets, session revocation checks, two-factor challenges and captchas";
#else
        const string cacheBackedState = "server-side session tickets";
#endif
        app.Logger.LogWarning(
            "ConnectionStrings:Redis is not configured, so the distributed cache falls back to process memory: {CacheBackedState} " +
            "are kept per process. This is only valid for a single instance; configure ConnectionStrings:Redis before running more replicas.",
            cacheBackedState);
    }
#endif

    app.UseForwardedHeaders();
#if (IncludeLocalization)
    // 在所有读取当前区域性的中间件之前解析请求区域性。
    app.UseJsonRequestLocalization();
#endif
#if (SpaFrontend)
    app.UseDefaultFiles();
    app.UseStaticFiles(SpaExtensions.CreateSpaStaticFileOptions());
#endif

    var requestLogging = app.Services.GetRequiredService<IOptionsMonitor<RequestLoggingOptions>>();
    app.UseCorrelationId();
    app.UseSerilogRequestLogging(options =>
    {
        // 宿主 logger 独立持有（preserveStaticLogger）：不指定时中间件写进启动期的静态 logger，
        // 请求完成事件就丢掉配置里的输出格式、sink 与 enrich
        // 与 Microsoft.Extensions.Logging.ILogger 同名，这里保留全限定名
        options.Logger = app.Services.GetRequiredService<Serilog.ILogger>();
        options.GetLevel = (httpContext, elapsed, ex) =>
        {
            if (ex != null || httpContext.Response.StatusCode >= 500)
                return LogEventLevel.Error;

            // 正常完成的请求记成哪一级由设置决定：调到 Verbose 就等于关掉请求日志
            // （全局最小级别通常是 Information，Verbose 不会落盘）。
            // 失败与 5xx 不受它影响——那是排障必需的，不该被一个运维开关关掉。
            return requestLogging.CurrentValue.Level;
        };
    });
    app.UseGlobalExceptionHandler();
    // 框架只写状态码、不写响应体的失败（生产环境的请求体解析失败、415、未匹配路由、认证质询、限流）
    // 经问题详情管道补上标准响应体与 traceId。只作用于 API：SPA 页面与静态资源的 404 不改写成 JSON。
    app.UseWhen(
        context => context.Request.Path.StartsWithSegments("/api"),
        api => api.UseStatusCodePages());
    app.MapHealthChecks("/api/health/live", new HealthCheckOptions
    {
        Predicate = registration => registration.Tags.Contains("live")
    }).AllowAnonymous();
    app.MapHealthChecks("/api/health/ready", new HealthCheckOptions
    {
        Predicate = registration => registration.Tags.Contains("ready")
    }).AllowAnonymous();

    app.UseRouting();
    app.UseCors();

#if (!SpaFrontend && (IncludeNotifications || IncludeRealTime))
    // 浏览器连 Hub 只能把 Bearer 放在查询串：只在 Hub 端点上转成 Authorization 头，普通 API 仍只认请求头
    app.UseHubAccessToken();
#endif
    app.UseAuthentication();
#if (SpaFrontend)
    // 浏览器会话靠 Cookie：跨源写请求与 Hub 握手只接受本源与登记的前端源
    app.UseBrowserOriginProtection();
#endif
#if (LocalIdentity && IncludeMultiTenancy)
    // 租户失效时注销 Cookie，避免会话困在不可用租户中。
    app.UseTenantSessionRecovery(options => options.SignOutScheme = AuthenticationSchemeNames.SessionCookie);
#endif
    // 租户在认证后、授权前解析；未解析到租户表示宿主上下文。
#if (IncludeMultiTenancy)
    app.UseMultiTenancy();
#else
    app.UseMiddleware<HostPrincipalMiddleware>();
#endif
    // 请求完成日志在租户作用域之外写出：经 Serilog 诊断上下文补上租户，键与 ICurrentTenant.Change 打开的日志作用域一致
    app.Use((context, next) =>
    {
        context.RequestServices.GetRequiredService<IDiagnosticContext>()
            .Set(TenantLogKeys.TenantId, context.RequestServices.GetRequiredService<ICurrentTenant>().Id);
        return next(context);
    });
#if (!LocalIdentity)
    // 必须在 UseMultiTenancy() 之后：用户行是 IMultiTenant，租户没解析出来会落成宿主行。
    app.UseMiddleware<ResourceUserProvisioningMiddleware>();
#endif
#if (LocalIdentity)
    // 受限会话（租户要求两步验证而本人未启用）只放行完成设置所需的接口
    app.UseMiddleware<TwoFactorSetupEnforcementMiddleware>();
#endif
    app.UseAuthorization();
    app.UseOperationFailureRecording();

    app.MapControllers();
    // 组件自带的端点（设置、权限、操作记录、通知、租户与租户连接），路由与原控制器一致
    app.MapComponentEndpoints();
    if (app.Environment.IsDevelopment())
    {
        // 本机查看接口：/openapi/v1.json。其他环境不暴露接口清单
        app.MapOpenApi().AllowAnonymous();
    }

#if (IncludeRealTime)
    // 业务事件与（启用时的）通知都经实时 Hub 推送，只映射这一个
    app.MapRealTimeHub();
#elif (IncludeNotifications)
    app.MapNotificationHub();
#endif

#if (SpaFrontend)
    app.MapMyProjectSpaFallback();
#endif

    // API 只验证 schema；所有 DDL 由 DbMigrator 施加。
    await app.VerifyDatabaseSchemaAsync();

    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    // 重抛以便宿主和测试观察到非零退出。
    Log.Fatal(ex, "Application terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

// 向 WebApplicationFactory 集成测试公开顶层语句生成的入口类型。
public partial class Program
{
}
