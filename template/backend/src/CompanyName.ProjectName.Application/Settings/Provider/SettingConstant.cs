#if (IncludeNotifications)
using CompanyName.ProjectName.Application.Notifications.Constants;
using Leistd.Notifications.Settings.Options;

#endif
namespace CompanyName.ProjectName.Application.Settings.Provider;

/// <summary>设置名称常量。</summary>
/// <remarks>
/// 设置名是跨前后端的字符串契约，集中在此避免两侧各写一份字面量后漂移。
/// </remarks>
public static class SettingConstant
{
    /// <summary>设置分组标识。</summary>
    /// <remarks>
    /// <para>分组决定设置页左侧的分类，词条键按 <c>SettingGroup:{标识}</c> 推导。
    /// 与设置名一样是跨前后端的字符串契约：界面按标识选中分类，因此它不能随文案改动。</para>
    /// <para>按管理员要完成的事归组，不按组件归组：新设置先归入已有分组。系统设置的每个分组对应一条管理子权限，
    /// 见 <see cref="SettingGroupPermissions"/>。</para>
    /// </remarks>
    public static class Groups
    {
        /// <summary>界面显示：语言、时区这类"怎么呈现"的偏好。</summary>
        public const string Display = "Display";

        /// <summary>运维：进程怎么跑的开关（日志级别等），多为进程级。</summary>
        public const string Operations = "Operations";

#if (IncludeOperationRecords || IncludeNotifications)
        /// <summary>数据保留：过期数据（操作记录、通知）保留多久、是否清理，进程级。</summary>
        public const string Retention = "Retention";
#endif

#if (LocalIdentity)
        /// <summary>注册与验证：租户各自的注册策略。</summary>
        public const string Registration = "Registration";

        /// <summary>登录与会话：租户各自的登录失败锁定、两步验证与会话策略。</summary>
        public const string Security = "Security";

#if (Email)
        /// <summary>邮件发送：进程级的 SMTP 参数。</summary>
        public const string Email = "Email";

#endif
#if (ExternalLogin)
        /// <summary>外部登录：进程级的第三方登录开关与客户端凭据。</summary>
        public const string ExternalLogin = "ExternalLogin";

#endif
#endif
#if (IncludeNotifications)
        /// <summary>通知偏好：每个人各自决定哪类通知经哪个渠道收。</summary>
        public const string Notifications = "Notifications";

#endif
        /// <summary>没写分组的设置的归处，避免它落在界面之外。</summary>
        public const string Other = "Other";
    }

    /// <summary>界面显示：组织给默认，个人可覆盖。</summary>
    public static class Display
    {
#if (IncludeLocalization)
        /// <summary>界面语言（culture 名，如 <c>zh-CN</c>）。</summary>
        public const string Language = "Display.Language";

        /// <summary>支持的界面语言，即 <see cref="Language"/> 的候选值。</summary>
        /// <remarks>与前端 <c>SUPPORTED_LANGS</c> 和 Program 里的 <c>AddJsonLocalization</c> 读同一份：任一漂移都会让某个语言只在一半链路上可用。</remarks>
        public static readonly IReadOnlyList<string> SupportedLanguages = ["en", "zh-CN"];

#endif
        /// <summary>展示时区（IANA 名，如 <c>Asia/Shanghai</c>）。时间一律以 UTC 存储，仅展示时换算。</summary>
        public const string TimeZone = "Display.TimeZone";
    }

    /// <summary>日志开关。</summary>
    /// <remarks>
    /// 都是<b>进程级</b>（<c>SettingScopes.Host</c>）：一个进程只有一个 logger，
    /// 按租户各存一份根本无从生效。改完立即对本进程生效，其它实例在下一次刷新周期跟上
    /// （设置组件的宿主级设置刷新，见 <c>Leistd:Settings:Hosting:RefreshInterval</c>）。
    /// </remarks>
    public static class Logging
    {
        /// <summary>全局最小日志级别。</summary>
        public const string MinimumLevel = "Logging.MinimumLevel";

        /// <summary>正常完成的请求记成哪一级；调高即等于关掉请求日志。</summary>
        public const string RequestLevel = "Logging.RequestLevel";

        /// <summary>合法的日志级别取值，与 Serilog 的 <c>LogEventLevel</c> 一致。</summary>
        /// <remarks>
        /// 定义的默认值、写入校验与界面候选项都读这一份：分开写几份时，
        /// 加一档只改了一处，就会变成"选得出却写不进"或者反过来。
        /// </remarks>
        public static readonly IReadOnlyList<string> Levels =
            ["Verbose", "Debug", "Information", "Warning", "Error", "Fatal"];
    }

#if (IncludeOperationRecords)
    /// <summary>操作记录保留期。</summary>
    /// <remarks>
    /// <b>进程级</b>：归档由一个后台任务跨租户统一执行，按租户各存一份无从生效。
    /// 设置值经宿主级设置配置源覆盖 <c>appsettings</c> 的 <c>Leistd:OperationRecords:Retention</c> 节，代码默认值就是该节的部署基线；
    /// 归档任务每轮取 Options 的当前值，下一轮即生效。执行时刻与批大小是部署调优参数，仍只在配置里。
    /// </remarks>
    public static class Audit
    {
        /// <summary>是否把超出保留期的操作记录搬入归档表。</summary>
        public const string RetentionEnabled = "Audit.RetentionEnabled";

        /// <summary>保留天数：早于"当前时刻减去该天数"的记录会被搬入归档表。</summary>
        public const string RetentionDays = "Audit.RetentionDays";

    }
#endif
#if (IncludeNotifications)
    /// <summary>通知保留期。</summary>
    /// <remarks>
    /// <b>进程级</b>：清理由一个后台任务逐库统一执行。设置值经宿主级设置配置源覆盖 <c>appsettings</c> 的
    /// <c>Leistd:Notifications:Retention</c> 节，代码默认值就是该节的部署基线；清理任务每轮取 Options 的当前值。
    /// 未读的保留天数不得短于已读的——否则未读通知先于已读被删。执行时刻与批大小是部署调优参数，仍只在配置里。
    /// </remarks>
    public static class NotificationRetention
    {
        /// <summary>是否清理超出保留期的通知。</summary>
        public const string Enabled = "Notifications.RetentionEnabled";

        /// <summary>已读通知保留天数。</summary>
        public const string ReadDays = "Notifications.ReadRetentionDays";

        /// <summary>未读通知保留天数，不短于已读的。</summary>
        public const string UnreadDays = "Notifications.UnreadRetentionDays";
    }
#endif

#if (LocalIdentity)
    /// <summary>注册与验证策略。</summary>
    /// <remarks>
    /// 租户级：每个租户可以有自己的注册策略。代码默认值取自 <c>appsettings</c> 的
    /// <c>UserRegistration</c> 段——配置仍是部署基线，设置只是租户级覆盖，
    /// 不另立一份真相。
    /// </remarks>
    public static class Registration
    {
        /// <summary>是否开放自助注册；不影响外部登录首次登录自动建号。</summary>
        public const string Enabled = "Registration.Enabled";

#if (Email)
        /// <summary>是否要求邮箱验证。</summary>
        public const string EnableEmailVerification = "Registration.EnableEmailVerification";

#endif
        /// <summary>图形验证码有效期（分钟）。</summary>
        public const string CaptchaExpiryMinutes = "Registration.CaptchaExpiryMinutes";
#if (Email)

        /// <summary>邮箱验证码有效期（分钟）。</summary>
        public const string EmailCodeExpiryMinutes = "Registration.EmailCodeExpiryMinutes";

        /// <summary>发送邮箱验证码的最小间隔（秒）。</summary>
        public const string EmailCodeSendIntervalSeconds = "Registration.EmailCodeSendIntervalSeconds";

        /// <summary>单个验证挑战允许的最大错误次数。</summary>
        public const string EmailCodeMaxAttempts = "Registration.EmailCodeMaxAttempts";
#endif

    }

    /// <summary>登录安全策略（租户级）。</summary>
    /// <remarks>代码默认值即推荐值，不另设配置节：它们不是部署环境相关的参数。</remarks>
    public static class Security
    {
        /// <summary>连续登录失败多少次后锁定账号；0 表示不锁定。</summary>
        public const string LockoutMaxFailedAttempts = "Security.LockoutMaxFailedAttempts";

        /// <summary>登录失败锁定的时长（分钟）。</summary>
        public const string LockoutDurationMinutes = "Security.LockoutDurationMinutes";

        /// <summary>是否要求所有人启用两步验证。</summary>
        public const string RequireTwoFactor = "Security.RequireTwoFactor";

        /// <summary>会话空闲多少天后失效。</summary>
        public const string SessionIdleTimeoutDays = "Security.SessionIdleTimeoutDays";

        /// <summary>连续失败次数的默认阈值。</summary>
        public const int DefaultLockoutMaxFailedAttempts = 5;

        /// <summary>锁定时长的默认值（分钟）。</summary>
        public const int DefaultLockoutDurationMinutes = 15;

    }

#if (Email)
    /// <summary>发信参数（SMTP）。</summary>
    /// <remarks>
    /// <para><b>进程级</b>（<c>SettingScopes.Host</c>）：发信通道是整个部署共用的，不按租户各配一套。</para>
    /// <para>设置值经宿主级设置配置源覆盖配置文件的 <c>Leistd:Email:Smtp</c> 节，发信端照常读 <c>IOptionsMonitor&lt;SmtpOptions&gt;</c>；
    /// 代码默认值是该节的部署基线：配置给基线，设置表给运行期覆盖，清除覆盖值即回到基线。
    /// 口令例外——它是机密设置，没有默认值，未设置时就是配置里的口令。</para>
    /// </remarks>
    public static class Email
    {
        /// <summary>SMTP 主机。</summary>
        public const string SmtpHost = "Email.SmtpHost";

        /// <summary>SMTP 端口。</summary>
        public const string SmtpPort = "Email.SmtpPort";

        /// <summary>是否启用传输层加密（465 用隐式 TLS，其余端口用 STARTTLS）。</summary>
        public const string SmtpEnableSsl = "Email.SmtpEnableSsl";

        /// <summary>登录用户名；留空表示匿名投递。</summary>
        public const string SmtpUsername = "Email.SmtpUsername";

        /// <summary>登录口令。机密设置：加密落库、只写不读。</summary>
        public const string SmtpPassword = "Email.SmtpPassword";

        /// <summary>默认发件地址。</summary>
        public const string DefaultFromAddress = "Email.DefaultFromAddress";

        /// <summary>默认发件显示名。</summary>
        public const string DefaultFromName = "Email.DefaultFromName";
    }
#endif
#if (ExternalLogin)

    /// <summary>外部登录提供方（GitHub、Google）的启用开关与客户端凭据。</summary>
    /// <remarks>
    /// <para><b>进程级</b>（<c>SettingScopes.Host</c>）：认证方案与回调地址整个部署共用，不按租户各配一套。</para>
    /// <para>设置值经宿主级设置配置源覆盖配置文件的 <c>ExternalAuth</c> 节，认证注册照常读 <c>IOptionsMonitor&lt;ExternalAuthOptions&gt;</c>；
    /// 开关的代码默认值是部署基线，客户端密钥是机密设置、没有默认值，未设置时就是配置里的密钥。
    /// 开关打开且两项凭据齐全时提供方才可用；凭据不全时只是不提供该入口，不拒绝写入。</para>
    /// </remarks>
    public static class ExternalLogin
    {
        /// <summary>是否启用 GitHub 登录。</summary>
        public const string GithubEnabled = "ExternalLogin.Github.Enabled";

        /// <summary>GitHub OAuth App 的 Client ID。</summary>
        public const string GithubClientId = "ExternalLogin.Github.ClientId";

        /// <summary>GitHub OAuth App 的 Client Secret。机密设置：加密落库、只写不读。</summary>
        public const string GithubClientSecret = "ExternalLogin.Github.ClientSecret";

        /// <summary>是否启用 Google 登录。</summary>
        public const string GoogleEnabled = "ExternalLogin.Google.Enabled";

        /// <summary>Google OAuth 客户端的 Client ID。</summary>
        public const string GoogleClientId = "ExternalLogin.Google.ClientId";

        /// <summary>Google OAuth 客户端的 Client Secret。机密设置：加密落库、只写不读。</summary>
        public const string GoogleClientSecret = "ExternalLogin.Google.ClientSecret";
    }
#endif

#endif
#if (IncludeNotifications)
    /// <summary>通知偏好（用户级），命名为 <c>Notifications.{通知类别}.{渠道}</c>。</summary>
    /// <remarks>
    /// 类别即通知的 <c>Type</c>，渠道即 <c>INotificationChannel.Name</c>：通知偏好组件按这两者拼出设置名，
    /// 读<b>收件人</b>的生效值；没有对应设置的组合一律投递——新加一个类别不会因为忘了定义偏好而收不到。
    /// 安全提醒的站内通知是必达组合（见 Program 里的 <c>AddNotificationPreferences</c>），因此没有 <c>Notifications.Security.InApp</c>。
    /// </remarks>
    public static class Notifications
    {
        private const string Prefix = NotificationPreferenceOptions.DefaultSettingNamePrefix;

#if (Email)
        /// <summary>安全提醒是否也发邮件（只发到已验证的邮箱）。</summary>
        public const string SecurityEmail = $"{Prefix}.{AppNotificationTypes.Security}.{AppNotificationChannels.Email}";

#endif
        /// <summary>系统通知是否在站内接收。</summary>
        public const string SystemInApp = $"{Prefix}.{AppNotificationTypes.System}.{AppNotificationChannels.InApp}";
#if (Email)

        /// <summary>系统通知是否也发邮件。</summary>
        public const string SystemEmail = $"{Prefix}.{AppNotificationTypes.System}.{AppNotificationChannels.Email}";
#endif
    }

#endif
}
