using CompanyName.ProjectName.Application.Settings.Options;
using Leistd.Settings.Definitions;
using Microsoft.Extensions.Options;
#if (LocalIdentity)
using System.Globalization;
using CompanyName.ProjectName.Application.Users.Options;
using CompanyName.ProjectName.Domain.Auth.Options;
#endif

namespace CompanyName.ProjectName.Application.Settings.Provider;

/// <summary>设置定义提供器。</summary>
/// <remarks>
/// 这里只声明<b>运行期可改</b>的业务偏好，以及管理员运行期维护的第三方集成客户端凭据（发信口令、外部登录的
/// Client ID 与 Client Secret，边界见设置组件文档）。连接串、签名与摘要密钥、认证协议参数等部署期配置一律留在
/// <c>appsettings</c> 与 <c>IOptions&lt;T&gt;</c>：把它们搬进设置表等于给运行期一个能悄悄
/// 拆掉安全与正确性保证的开关。
/// <para>已经是某个实体字段的东西也不要再定义成设置——例如租户显示名归
/// <c>TenantRecord.DisplayName</c>，在租户管理里改；两处都能写就有两个互相矛盾的事实源。</para>
/// <para>displayName 存的是<b>人类可读文案</b>，不是本地化键：关掉本地化的项目里没有翻译器，
/// 存键会让界面直接显示 <c>Setting:Display.Language</c> 这样的内部标识。
/// 本地化项目按 <c>Setting:{name}</c> 查词条翻译，查不到才回落到这里的文案——
/// 键由名称推导，不必在定义里再抄一遍。</para>
/// <para>设置页的面板顺序取接口里各分组首次出现的先后，接口按这里的定义顺序返回。显示（语言与时区）在最前：
/// 它同时是个人偏好的第一段；系统设置其余各段按管理员的使用频率排：注册、登录与会话、外部登录、邮件发送，
/// 运维与数据保留在最后。</para>
/// </remarks>
/// <para>值域（布尔、整数区间、候选值）声明在定义上：设置组件在写入端据此校验，设置页据此渲染控件。
/// 覆盖部署配置的宿主级设置（日志级别、审计、发信参数、外部登录）不写默认值——设置组件按绑定取部署基线
/// （见 Api 的 <c>HostSettingBindings</c>），清除覆盖值即回落到部署配置，而不是回落到一个写死的值。</para>
/// <param name="displayDefaults">界面语言与时区的部署基线：两项的代码默认值取自它，空为跟随设备。</param>
#if (LocalIdentity)
/// <param name="registrationOptions">
/// 注册策略的部署基线：注册类设置的代码默认值取自它，因此 <c>appsettings</c> 仍是
/// 部署期唯一的真相，设置表只承载租户级覆盖。
/// </param>
/// <param name="sessionOptions">会话空闲时限的部署基线（组合根按会话 Cookie 的有效期配置）。</param>
public class SettingDefinitionProvider(
    IOptions<DisplayDefaultsOptions> displayDefaults,
    IOptions<UserRegistrationOptions> registrationOptions,
    IOptions<UserSessionOptions> sessionOptions) : ISettingDefinitionProvider
{
#else
public class SettingDefinitionProvider(IOptions<DisplayDefaultsOptions> displayDefaults) : ISettingDefinitionProvider
{
#endif
    /// <inheritdoc />
    public void Define(ISettingDefinitionContext context)
    {
        var display = displayDefaults.Value;
#if (IncludeLocalization)
        // 界面语言：组织给默认、个人可覆盖。解析顺序 个人 → 组织 → 部署基线 → 设备。
        //
        // 默认值（部署基线）通常留空，含义是"跟随系统"：同一组织里既有说中文的也有说英文的，
        // 浏览器的语言偏好（navigator.languages）比统一默认更准；写死 "en" 会让中文浏览器登录后也被切成英文。
        // 组织值用于有统一工作语言的组织，也让服务端在没有浏览器可参考时（发给收件人的邮件、提醒）有据可依。
        context.Add(
            SettingConstant.Display.Language,
            defaultValue: Normalize(display.Language),
            scopes: SettingScopes.All,
            displayName: "Language",
            group: SettingConstant.Groups.Display)
            .WithAllowedValues(SettingConstant.Display.SupportedLanguages)
            .IsVisibleToClients = true;

#endif
        // 时区同理：组织给默认、个人可覆盖。都没设时浏览器用自己的时区
        // （`Intl.DateTimeFormat().resolvedOptions().timeZone`），服务端产出文本时回落到 UTC（见 UserTimeZoneProvider）。
        context.Add(
            SettingConstant.Display.TimeZone,
            defaultValue: Normalize(display.TimeZone),
            scopes: SettingScopes.All,
            displayName: "Time zone",
            group: SettingConstant.Groups.Display).IsVisibleToClients = true;

#if (LocalIdentity)

        // 注册策略按租户：同一套部署下，不同租户可以有不同的注册门槛。
        // 默认值取自 appsettings 的 UserRegistration 段，配置仍是部署基线。
        var registration = registrationOptions.Value;

        context.Add(
            SettingConstant.Registration.Enabled,
            defaultValue: registration.Enabled ? "true" : "false",
            scopes: SettingScopes.Tenant,
            displayName: "Allow self sign-up",
            group: SettingConstant.Groups.Registration)
            .AsBoolean()
            .IsVisibleToClients = true;

#if (Email)
        context.Add(
            SettingConstant.Registration.EnableEmailVerification,
            defaultValue: registration.EnableEmailVerification ? "true" : "false",
            scopes: SettingScopes.Tenant,
            displayName: "Require email verification",
            group: SettingConstant.Groups.Registration)
            .AsBoolean()
            .IsVisibleToClients = true;

#endif

        context.Add(
            SettingConstant.Registration.CaptchaExpiryMinutes,
            defaultValue: registration.CaptchaExpiryMinutes.ToString(CultureInfo.InvariantCulture),
            scopes: SettingScopes.Tenant,
            displayName: "Captcha lifetime (minutes)",
            group: SettingConstant.Groups.Registration)
            .AsInteger(1, 60)
            .IsVisibleToClients = true;

#if (Email)
        context.Add(
            SettingConstant.Registration.EmailCodeExpiryMinutes,
            defaultValue: registration.EmailCodeExpiryMinutes.ToString(CultureInfo.InvariantCulture),
            scopes: SettingScopes.Tenant,
            displayName: "Email code lifetime (minutes)",
            group: SettingConstant.Groups.Registration)
            .AsInteger(1, 60)
            .IsVisibleToClients = true;

        context.Add(
            SettingConstant.Registration.EmailCodeSendIntervalSeconds,
            defaultValue: registration.EmailCodeSendIntervalSeconds.ToString(CultureInfo.InvariantCulture),
            scopes: SettingScopes.Tenant,
            displayName: "Email code send interval (seconds)",
            group: SettingConstant.Groups.Registration)
            .AsInteger(1, 3600)
            .IsVisibleToClients = true;

        context.Add(
            SettingConstant.Registration.EmailCodeMaxAttempts,
            defaultValue: registration.EmailCodeMaxAttempts.ToString(CultureInfo.InvariantCulture),
            scopes: SettingScopes.Tenant,
            displayName: "Email code max attempts",
            group: SettingConstant.Groups.Registration)
            .AsInteger(1, 20)
            .IsVisibleToClients = true;
#endif

        // 登录失败锁定按租户：对外开放注册的租户与只有内部员工的租户，能接受的门槛不同。
        context.Add(
            SettingConstant.Security.LockoutMaxFailedAttempts,
            defaultValue: SettingConstant.Security.DefaultLockoutMaxFailedAttempts.ToString(CultureInfo.InvariantCulture),
            scopes: SettingScopes.Tenant,
            displayName: "Lock the account after this many failed sign-ins (0 = never)",
            group: SettingConstant.Groups.Security)
            .AsInteger(0, 100)
            .IsVisibleToClients = true;

        context.Add(
            SettingConstant.Security.LockoutDurationMinutes,
            defaultValue: SettingConstant.Security.DefaultLockoutDurationMinutes.ToString(CultureInfo.InvariantCulture),
            scopes: SettingScopes.Tenant,
            displayName: "Lockout duration (minutes)",
            group: SettingConstant.Groups.Security)
            .AsInteger(1, 1440)
            .IsVisibleToClients = true;

        // 开启后，没启用两步验证的人的会话即受限（已登录的会话在下一个请求生效，包括打开开关的管理员本人），
        // 必须先完成设置才能做别的事；关闭后下一个请求即解除。
        // 默认关闭：打开它会让所有尚未设置的人立即被拦下，这得是管理员有意的决定
        context.Add(
            SettingConstant.Security.RequireTwoFactor,
            defaultValue: "false",
            scopes: SettingScopes.Tenant,
            displayName: "Require two-factor authentication for everyone",
            group: SettingConstant.Groups.Security)
            .AsBoolean()
            .IsVisibleToClients = true;
#if (Email)

        // 邮箱只作补充的第二步：能进邮箱往往只凭一个密码，所以它不满足上面的"要求所有人启用"，
        // 也默认关闭，由管理员有意打开。关闭后只开了邮箱方式的人回到单步登录，个人设置里写明原因
        context.Add(
            SettingConstant.Security.AllowEmailTwoFactor,
            defaultValue: "false",
            scopes: SettingScopes.Tenant,
            displayName: "Allow email codes as a sign-in second step",
            group: SettingConstant.Groups.Security)
            .AsBoolean()
            .IsVisibleToClients = true;
#endif

        // 会话空闲多久失效：租户各自的安全策略。基线取自部署的会话 Cookie 有效期；
        // 调短后在途会话下一次实际校验（最多隔一个校验缓存周期）按新时长判定，调长后续发的 Cookie 跟上新时长
        context.Add(
            SettingConstant.Security.SessionIdleTimeoutDays,
            defaultValue: ((int)sessionOptions.Value.IdleTimeout.TotalDays).ToString(CultureInfo.InvariantCulture),
            scopes: SettingScopes.Tenant,
            displayName: "Sign out after this many idle days",
            group: SettingConstant.Groups.Security)
            .AsInteger(1, 365)
            .IsVisibleToClients = true;

#if (ExternalLogin)

        // 外部登录也是进程级的：认证方案与回调地址整个部署共用。开关默认值取部署基线（模板基线为关闭）；
        // 客户端密钥是机密设置，没有默认值，未设置时用配置里的密钥
        AddExternalLogin(context, SettingConstant.ExternalLogin.GithubEnabled, "Enable GitHub sign-in").AsBoolean();
        AddExternalLogin(context, SettingConstant.ExternalLogin.GithubClientId, "GitHub client ID");
        AddExternalLogin(context, SettingConstant.ExternalLogin.GithubClientSecret, "GitHub client secret").IsEncrypted = true;
        AddExternalLogin(context, SettingConstant.ExternalLogin.GoogleEnabled, "Enable Google sign-in").AsBoolean();
        AddExternalLogin(context, SettingConstant.ExternalLogin.GoogleClientId, "Google client ID");
        AddExternalLogin(context, SettingConstant.ExternalLogin.GoogleClientSecret, "Google client secret").IsEncrypted = true;
#endif
#if (Email)

        // 发信参数是进程级的：整个部署共用一个发信通道。默认值取部署基线，与日志级别同一口径
        AddEmail(context, SettingConstant.Email.SmtpHost, "SMTP host");
        AddEmail(context, SettingConstant.Email.SmtpPort, "SMTP port").AsInteger(1, 65535);
        AddEmail(context, SettingConstant.Email.SmtpEnableSsl, "Use TLS").AsBoolean();
        AddEmail(context, SettingConstant.Email.SmtpUsername, "SMTP username");
        // 口令是机密设置：加密落库，界面只写不读；没有默认值，未设置时发信端用配置里的口令
        AddEmail(context, SettingConstant.Email.SmtpPassword, "SMTP password").IsEncrypted = true;
        AddEmail(context, SettingConstant.Email.DefaultFromAddress, "Sender address");
        AddEmail(context, SettingConstant.Email.DefaultFromName, "Sender name");
#endif
#endif
        // 日志是**进程级**的：一个进程只有一个 logger，按租户各存一份无从生效，
        // 因此用 Host 层——租户上下文的写入会被就地拒绝，而不是写进去不起作用。
        //
        // 默认值取部署基线（Serilog 配置节），与注册策略取 appsettings 是同一个口径：
        // 配置给基线、设置表给运行期覆盖。写死成 Information 的话，部署把级别配成
        // Warning 也会被顶掉，而清除覆盖值同样回不到部署基线。
        context.Add(
            SettingConstant.Logging.MinimumLevel,
            scopes: SettingScopes.Host,
            displayName: "Minimum log level",
            group: SettingConstant.Groups.Operations)
            .WithAllowedValues(SettingConstant.Logging.Levels)
            .IsVisibleToClients = true;

        // 正常完成的请求记成哪一级。调到 Verbose 就等于关掉请求日志——
        // 全局最小级别通常是 Information，Verbose 的记录不会落盘。
        context.Add(
            SettingConstant.Logging.RequestLevel,
            scopes: SettingScopes.Host,
            displayName: "Request log level",
            group: SettingConstant.Groups.Operations)
            .WithAllowedValues(SettingConstant.Logging.Levels)
            .IsVisibleToClients = true;

#if (IncludeOperationRecords)
        // 操作记录保留期同样是进程级：归档任务跨租户统一执行。默认值取部署基线，
        // 部署没打开时这里也是关——一个默认就会动审计数据的开关不该由代码替部署决定。
        context.Add(
            SettingConstant.Audit.RetentionEnabled,
            scopes: SettingScopes.Host,
            displayName: "Archive expired operation records",
            group: SettingConstant.Groups.Retention)
            .AsBoolean()
            .IsVisibleToClients = true;

        // 区间与操作记录组件保留期选项上的校验一致：设置值经配置源进入该选项，越界会让整组设置不生效
        context.Add(
            SettingConstant.Audit.RetentionDays,
            scopes: SettingScopes.Host,
            displayName: "Operation record retention (days)",
            group: SettingConstant.Groups.Retention)
            .AsInteger(30, 3650)
            .IsVisibleToClients = true;
#endif
#if (IncludeNotifications)

        // 通知保留期同样是进程级，与操作记录保留期同组。区间与通知组件保留期选项上的校验一致，
        // "未读不短于已读"由 NotificationRetentionSettingValidator 把关
        context.Add(
            SettingConstant.NotificationRetention.Enabled,
            scopes: SettingScopes.Host,
            displayName: "Clean up expired notifications",
            group: SettingConstant.Groups.Retention)
            .AsBoolean()
            .IsVisibleToClients = true;

        context.Add(
            SettingConstant.NotificationRetention.ReadDays,
            scopes: SettingScopes.Host,
            displayName: "Read notification retention (days)",
            group: SettingConstant.Groups.Retention)
            .AsInteger(1, 3650)
            .IsVisibleToClients = true;

        context.Add(
            SettingConstant.NotificationRetention.UnreadDays,
            scopes: SettingScopes.Host,
            displayName: "Unread notification retention (days)",
            group: SettingConstant.Groups.Retention)
            .AsInteger(1, 3650)
            .IsVisibleToClients = true;

        // 通知偏好是个人的：谁收什么由本人决定，不设租户默认值
#if (Email)
        AddNotificationPreference(context, SettingConstant.Notifications.SecurityEmail, true, "Security alerts by email");
#endif
        AddNotificationPreference(context, SettingConstant.Notifications.SystemInApp, true, "System notifications in the app");
#if (Email)
        AddNotificationPreference(context, SettingConstant.Notifications.SystemEmail, false, "System notifications by email");
#endif
#endif
    }

    // 部署基线空白即"未配置"：作为默认值时与没有默认值等价，交给设备决定
    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
#if (IncludeNotifications)

    private static void AddNotificationPreference(
        ISettingDefinitionContext context,
        string name,
        bool defaultValue,
        string displayName)
    {
        context.Add(
            name,
            defaultValue ? "true" : "false",
            SettingScopes.User,
            displayName,
            SettingConstant.Groups.Notifications)
            .AsBoolean()
            .IsVisibleToClients = true;
    }
#endif
#if (Email)

    private static ISettingDefinition AddEmail(ISettingDefinitionContext context, string name, string displayName)
    {
        var definition = context.Add(
            name,
            defaultValue: null,
            SettingScopes.Host,
            displayName,
            SettingConstant.Groups.Email);
        definition.IsVisibleToClients = true;
        return definition;
    }
#endif
#if (ExternalLogin)

    private static ISettingDefinition AddExternalLogin(ISettingDefinitionContext context, string name, string displayName)
    {
        var definition = context.Add(
            name,
            defaultValue: null,
            SettingScopes.Host,
            displayName,
            SettingConstant.Groups.ExternalLogin);
        definition.IsVisibleToClients = true;
        return definition;
    }
#endif
}
