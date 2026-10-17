namespace CompanyName.ProjectName.Application.OperationRecords.Provider;

/// <summary>稳定的业务操作码，回答"发生了什么动作"。</summary>
/// <remarks>
/// <para>框架只把它原样存下去、从不解释，因此由业务定义——它穷举不了业务词汇。
/// 界面按这个码本地化，<b>一旦发布就不要改</b>：它是历史记录的含义本身，改了等于篡改过去。</para>
/// <para><b>这里只列出有代表性的几个，不是固定清单。</b>值得留痕的是"改变了什么"的动作；
/// 查询、登录尝试、定时任务的例行心跳都不该记——审计表的价值来自密度，记满之后没人会去看。</para>
/// </remarks>
public static class OperationRecordActions
{
    /// <summary>创建用户。</summary>
    public const string UserCreated = "user.created";

    /// <summary>更新用户资料。</summary>
    public const string UserUpdated = "user.updated";

    /// <summary>删除用户。</summary>
    public const string UserDeleted = "user.deleted";

    /// <summary>管理员启用了用户。</summary>
    public const string UserEnabled = "user.enabled";

    /// <summary>管理员停用了用户：该用户的会话与令牌随之作废。</summary>
    public const string UserDisabled = "user.disabled";

#if (LocalIdentity)
    /// <summary>管理员重置了用户的密码：该用户的会话与令牌随之作废。</summary>
    public const string UserPasswordReset = "user.password-reset";

    /// <summary>管理员提前解除了用户的登录锁定。</summary>
    public const string UserUnlocked = "user.unlocked";

    /// <summary>管理员重置了用户的两步验证（用户丢了手机和恢复码时）。</summary>
    public const string UserTwoFactorReset = "user.two-factor-reset";
#endif

    /// <summary>替换用户的角色集合。与普通资料更新分开：它直接改变这个人能做什么。</summary>
    public const string UserRolesReplaced = "user.roles-replaced";

    /// <summary>创建角色。</summary>
    public const string RoleCreated = "role.created";

    /// <summary>更新角色资料（显示名、描述、排序、是否默认角色）。</summary>
    public const string RoleUpdated = "role.updated";

    /// <summary>删除角色。</summary>
    public const string RoleDeleted = "role.deleted";

    /// <summary>替换某个主体的权限授予。</summary>
    public const string PermissionGrantsReplaced = "permission-grants.replaced";

    /// <summary>租户创建。</summary>
    public const string TenantCreated = "tenant.created";

    /// <summary>租户资料更新。</summary>
    public const string TenantUpdated = "tenant.updated";

    /// <summary>租户启用或停用。改变的是"这一整批人还能不能进来"。</summary>
    public const string TenantActivationChanged = "tenant.activation-changed";

    /// <summary>租户删除。</summary>
    public const string TenantDeleted = "tenant.deleted";

    /// <summary>为租户登记了一条新的数据库连接。</summary>
    /// <remarks>
    /// 登记、改写、删除各自一个码：三者的安全含义不同——新增是多了一个落点，
    /// 改写意味着换库或轮换凭据，删除是把租户退回宿主库。合成一个码之后事后分不出是哪种。
    /// </remarks>
    public const string TenantConnectionRegistered = "tenant.connection-registered";

    /// <summary>改写了租户已有的数据库连接（换库或轮换凭据）。</summary>
    public const string TenantConnectionChanged = "tenant.connection-changed";

    /// <summary>删除了租户的数据库连接登记，该租户退回宿主库。</summary>
    public const string TenantConnectionRemoved = "tenant.connection-removed";

#if (LocalIdentity && IncludeMultiTenancy)
    /// <summary>部署作业恢复了租户管理员：撤回删除、重新启用、补回 Admin 成员关系，并把租户侧可用权限补齐给 Admin 角色。</summary>
    public const string TenantAdminRestored = "tenant.admin-restored";

#endif
#if (RemoteTokenAuth)
    /// <summary>部署作业把远端用户首次加入管理员角色。</summary>
    public const string ResourceAdminGranted = "resource.admin-granted";

    /// <summary>部署作业以显式恢复模式恢复了远端用户的管理员访问：撤回删除、重新启用、补回 Admin 成员关系，并补齐 Admin 角色的可用权限。</summary>
    public const string ResourceAdminRestored = "resource.admin-restored";
#endif

    /// <summary>设置变更。作用域（宿主／租户／用户）随目标标识带出。</summary>
    public const string SettingChanged = "setting.changed";

#if (LocalIdentity && Email)
    /// <summary>用当前发信参数发送了一封测试邮件。</summary>
    /// <remarks>
    /// 它不改任何状态，记它是为了被拒的那一半：没有权限却反复尝试、或在租户里尝试，
    /// 都要留下"谁在试"的痕迹。不带目标——收件人是任意填写的地址。
    /// </remarks>
    public const string SettingTestEmailSent = "setting.test-email-sent";
#endif

#if (OpenIddictServer)
    /// <summary>登记开放应用（OAuth 客户端）。</summary>
    public const string OpenApplicationCreated = "open-application.created";

    /// <summary>修改开放应用的类型、回调地址或授权能力。</summary>
    public const string OpenApplicationUpdated = "open-application.updated";

    /// <summary>删除开放应用，该客户端随即无法再取得令牌。</summary>
    public const string OpenApplicationDeleted = "open-application.deleted";

    /// <summary>重置开放应用的密钥：旧密钥立即失效。</summary>
    public const string OpenApplicationSecretReset = "open-application.secret-reset";

#endif
    /// <summary>导出操作记录。<b>导出审计日志这件事本身要被审计</b>——谁把历史带走了是安全事件。</summary>
    public const string OperationRecordsExported = "operation-records.exported";

#if (LocalIdentity)

    /// <summary>登录成功。</summary>
    public const string AuthLoginSucceeded = "auth.login.succeeded";

    /// <summary>登录失败。</summary>
    /// <remarks>
    /// <b>必须记</b>：OWASP 明列认证事件，"谁在反复尝试"正是审计最该回答的问题之一。
    /// 但它是<b>匿名写入面</b>——记录由未认证请求触发，因此必须与失败登录的折叠计数
    /// 一同存在，否则就是一个无需凭据即可刷爆审计表的洞。
    /// </remarks>
    public const string AuthLoginFailed = "auth.login.failed";

    /// <summary>账号因连续登录失败被临时锁定（目标为被锁定的账号）。</summary>
    /// <remarks>与登录失败同为匿名写入面，但一个锁定期内至多触发一次，写入量有界。</remarks>
    public const string AuthLockedOut = "auth.locked-out";

    /// <summary>用户自助修改密码。</summary>
    public const string AuthPasswordChanged = "auth.password.changed";

    /// <summary>用户更换或清除了自己的头像。</summary>
    public const string AuthAvatarChanged = "auth.avatar.changed";

    /// <summary>用户更换了自己的邮箱（换邮箱要求近期认证，失败也按这个动作留痕）。</summary>
    public const string AuthEmailChanged = "auth.email.changed";

#if (Email)
    /// <summary>用户用验证码确认了自己当前的邮箱。</summary>
    public const string AuthEmailVerified = "auth.email.verified";
#endif

    /// <summary>用户撤销了自己的某个登录会话（目标为该设备的 IP）。</summary>
    public const string AuthSessionRevoked = "auth.session.revoked";

    /// <summary>用户一次撤销了自己除当前以外的全部登录会话。</summary>
    public const string AuthOtherSessionsRevoked = "auth.sessions.others-revoked";

    /// <summary>用户启用了两步验证。</summary>
    public const string AuthTwoFactorEnabled = "auth.two-factor.enabled";

    /// <summary>用户换绑了身份验证器（新密钥与新的一组恢复码，旧的随即作废）。</summary>
    public const string AuthTwoFactorAuthenticatorReplaced = "auth.two-factor.authenticator-replaced";

    /// <summary>用户停用了两步验证。</summary>
    public const string AuthTwoFactorDisabled = "auth.two-factor.disabled";

    /// <summary>用户重新生成了恢复码（旧的全部作废）。</summary>
    public const string AuthTwoFactorRecoveryCodesRegenerated = "auth.two-factor.recovery-codes-regenerated";

    /// <summary>用恢复码完成了登录第二步。</summary>
    /// <remarks>值得单独留痕：恢复码是手机不在身边时的后门，被别人用掉时本人和管理员都该看得到。</remarks>
    public const string AuthTwoFactorRecoveryCodeUsed = "auth.two-factor.recovery-code-used";

#if (ExternalLogin)
    /// <summary>用户绑定了一个外部账号（目标为"提供商: 外部用户名"）。</summary>
    public const string AuthExternalLoginLinked = "auth.external-login.linked";

    /// <summary>用户解绑了一个外部账号。</summary>
    public const string AuthExternalLoginUnlinked = "auth.external-login.unlinked";

    /// <summary>只经外部登录建立的账号自助设置了第一个口令。</summary>
    public const string AuthPasswordSet = "auth.password.set";
#endif

    /// <summary>自助注册。</summary>
    public const string AuthRegistered = "auth.registered";

    /// <summary>宿主管理员开始以租户身份操作。</summary>
    /// <remarks>
    /// 可见性是 <b>租户级</b>而非宿主级：租户必须看得见"有人以我的名义进来了"，
    /// 否则审计表对被操作的一方是瞎的——租户管理员会看到自己的用户做了他没做过的事。
    /// </remarks>
    public const string ImpersonationStarted = "impersonation.started";

    /// <summary>结束模拟登录。</summary>
    /// <remarks>
    /// 记在<b>被模拟的租户</b>里，操作人是被模拟者、模拟者名是真实操作人——
    /// 与模拟期间的其他记录同一口径，租户由此看得到"那次进来的人什么时候走的"。
    /// </remarks>
    public const string ImpersonationEnded = "impersonation.ended";

    /// <summary>宿主侧：进入了某个租户（以其管理员身份）。</summary>
    /// <remarks>
    /// 与 <see cref="ImpersonationStarted"/> 是同一事件在两层各留的一条，不是重复：
    /// 那条回答租户"谁进了我这里"，这条回答宿主"我们的人进了哪个租户"。
    /// 目标是<b>租户</b>而不是租户里的账号——宿主侧要追的是进了哪家。
    /// </remarks>
    public const string TenantImpersonationStarted = "tenant.impersonation-started";

    /// <summary>宿主侧：结束了对某个租户的模拟。</summary>
    public const string TenantImpersonationEnded = "tenant.impersonation-ended";
#endif
#if (OpenIddictServer)

    /// <summary>签发访问令牌（含机器主体的 client_credentials）。</summary>
    public const string AuthTokenIssued = "auth.token.issued";
#endif
}

/// <summary>不由权限把守的操作，凭什么放行。</summary>
/// <remarks>
/// 授权依据绝大多数情况下就是权限名。但有些操作不由权限体系管辖，此时仍要留下依据——
/// 空值分不出"不需要权限"和"忘了记"。框架同样不替业务枚举这些标记。
/// </remarks>
public static class OperationRecordAuthorizations
{
    /// <summary>用户改自己的数据（改密码、绑定 MFA），凭的是"已认证且是本人"。</summary>
    public const string AuthenticatedSelf = "AuthenticatedSelf";

    /// <summary>给用户直接授予权限。</summary>
    /// <remarks>
    /// <b>刻意不复用某个权限名。</b>授予管理器支持 <c>User</c> 与 <c>Role</c> 两种主体
    /// （见 <c>PermissionGrantProviderNames</c>），但本项目只为角色侧开了端点，
    /// 用户侧没有对应的权限定义。凑一个语义不符的权限名（如"分配用户角色"）会在审计表里
    /// 留下一条撒谎的"凭什么"——而这种错编译器抓不到，只在事后追责时把人指错方向。
    /// 开出用户侧端点时，应先定义相应权限并改用那个权限名。
    /// </remarks>
    public const string DirectUserGrant = "DirectUserGrant";
#if (RemoteTokenAuth || LocalIdentity)
    /// <summary>持有数据库部署权限的命令行作业（DbMigrator 的管理员引导、恢复与两步验证重置）。</summary>
    public const string DeploymentBootstrap = "DeploymentBootstrap";
#endif
#if (LocalIdentity)

    /// <summary>出示了凭据（登录尝试），凭的既不是权限也不是"已认证"——此刻还没有主体。</summary>
    /// <remarks>
    /// 登录记录的"什么人"由<b>目标</b>承载而非操作人：登录请求发生时主体尚未建立
    /// （<c>SignInAsync</c> 只往响应里种 Cookie，本次请求的 <c>HttpContext.User</c> 仍是匿名），
    /// 操作人字段为空是诚实的，它如实表达"这是一次匿名请求"。
    /// </remarks>
    public const string CredentialsPresented = "CredentialsPresented";

    /// <summary>自助注册，凭的是租户的注册策略放行。</summary>
    public const string SelfRegistration = "SelfRegistration";
#endif
}
