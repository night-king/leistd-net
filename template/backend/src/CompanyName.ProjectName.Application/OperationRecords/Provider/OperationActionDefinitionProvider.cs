using Leistd.OperationRecords.Definitions;
using Leistd.OperationRecords.Models;

namespace CompanyName.ProjectName.Application.OperationRecords.Provider;

/// <summary>登记本项目的操作动作定义。</summary>
/// <remarks>
/// <para><b>与 <see cref="OperationRecordActions"/> 是两层，不是替代关系。</b>
/// 常量类仍然保留并被逐字引用——控制器上的 <c>[OperationRecordAction(...)]</c> 是特性参数，
/// 必须是<b>编译期常量</b>，换不成定义对象。这里是在常量之上补一层元数据
/// （类别、严重度、可见性），让界面能按类别筛选、让闸门能断言文案齐全、
/// 让 <c>Critical</c> 动作可以直接接告警。</para>
/// <para>可见性逐条显式声明，<b>没有默认值</b>：省略时静默落到"租户可见"，
/// 而宿主侧动作落成租户可见就是跨租户信息泄露，且只在真的建了租户之后才暴露。</para>
/// </remarks>
public class OperationActionDefinitionProvider : IOperationActionDefinitionProvider
{
    public void Define(IOperationActionDefinitionContext context)
    {
#if (RemoteTokenAuth)
        context.Add(OperationRecordActions.ResourceAdminGranted,
            OperationRecordCategories.Authorization, OperationVisibility.Tenant, OperationSeverity.Critical);
        context.Add(OperationRecordActions.ResourceAdminRestored,
            OperationRecordCategories.Authorization, OperationVisibility.Tenant, OperationSeverity.Critical);
#endif
#if (LocalIdentity && IncludeMultiTenancy)
        // 租户自己的管理员看得到：部署方在他们的租户里动了管理员账号与 Admin 角色
        context.Add(OperationRecordActions.TenantAdminRestored,
            OperationRecordCategories.Authorization, OperationVisibility.Tenant, OperationSeverity.Critical);
#endif
        // 账号：用户与角色自身的增删改。
        context.Add(
            OperationRecordActions.UserCreated,
            OperationRecordCategories.Account,
            OperationVisibility.Tenant);
        context.Add(
            OperationRecordActions.UserUpdated,
            OperationRecordCategories.Account,
            OperationVisibility.Tenant);
        context.Add(
            OperationRecordActions.UserDeleted,
            OperationRecordCategories.Account,
            OperationVisibility.Tenant,
            OperationSeverity.Notice);
        // 启停直接决定"这个人还能不能进来"，与租户启停同理，要能单独筛出来
        context.Add(
            OperationRecordActions.UserEnabled,
            OperationRecordCategories.Account,
            OperationVisibility.Tenant,
            OperationSeverity.Notice);
        context.Add(
            OperationRecordActions.UserDisabled,
            OperationRecordCategories.Account,
            OperationVisibility.Tenant,
            OperationSeverity.Notice);
        context.Add(
            OperationRecordActions.RoleCreated,
            OperationRecordCategories.Account,
            OperationVisibility.Tenant);
        context.Add(
            OperationRecordActions.RoleUpdated,
            OperationRecordCategories.Account,
            OperationVisibility.Tenant);
        context.Add(
            OperationRecordActions.RoleDeleted,
            OperationRecordCategories.Account,
            OperationVisibility.Tenant,
            OperationSeverity.Notice);

        // 授权：改变"谁能做什么"。两条都是 Critical——这是整套权限体系里最敏感的写操作，
        // 事后追责最先看的就是它们，必须能独立筛出来、能接告警。
        context.Add(
            OperationRecordActions.UserRolesReplaced,
            OperationRecordCategories.Authorization,
            OperationVisibility.Tenant,
            OperationSeverity.Critical);
        context.Add(
            OperationRecordActions.PermissionGrantsReplaced,
            OperationRecordCategories.Authorization,
            OperationVisibility.Tenant,
            OperationSeverity.Critical);

        // 租户：宿主侧动作，可见性必须是 Host。落成 Tenant 就是跨租户信息泄露——
        // 甲租户的管理员会看到乙租户被创建、被停用。
        context.Add(
            OperationRecordActions.TenantCreated,
            OperationRecordCategories.Tenant,
            OperationVisibility.Host,
            OperationSeverity.Notice);
        context.Add(
            OperationRecordActions.TenantUpdated,
            OperationRecordCategories.Tenant,
            OperationVisibility.Host);
        context.Add(
            OperationRecordActions.TenantActivationChanged,
            OperationRecordCategories.Tenant,
            OperationVisibility.Host,
            OperationSeverity.Critical);
        context.Add(
            OperationRecordActions.TenantDeleted,
            OperationRecordCategories.Tenant,
            OperationVisibility.Host,
            OperationSeverity.Critical);
        // 连接决定租户数据落在哪个库：改错它等于把数据写进别的库，与启停同级。
        // 三种变化分别成码，事后才分得出是新增落点、换库还是退回宿主库
        foreach (var connectionAction in new[]
                 {
                     OperationRecordActions.TenantConnectionRegistered,
                     OperationRecordActions.TenantConnectionChanged,
                     OperationRecordActions.TenantConnectionRemoved
                 })
        {
            context.Add(
                connectionAction,
                OperationRecordCategories.Tenant,
                OperationVisibility.Host,
                OperationSeverity.Critical);
        }

#if (OpenIddictServer)
        // 开放应用：决定哪个客户端能取得什么令牌，归"授权"类。它是宿主全局资源（OpenIddict 表不分租户），
        // 可见性必须是 Host，否则租户管理员会看到全系统的客户端变更。
        // 重置密钥是 Critical：旧密钥立即失效，拿到新密钥的人即拥有该客户端的全部能力。
        foreach (var openApplicationAction in new[]
                 {
                     OperationRecordActions.OpenApplicationCreated,
                     OperationRecordActions.OpenApplicationUpdated,
                     OperationRecordActions.OpenApplicationDeleted
                 })
        {
            context.Add(
                openApplicationAction,
                OperationRecordCategories.Authorization,
                OperationVisibility.Host,
                OperationSeverity.Notice);
        }

        context.Add(
            OperationRecordActions.OpenApplicationSecretReset,
            OperationRecordCategories.Authorization,
            OperationVisibility.Host,
            OperationSeverity.Critical);

#endif
        // 设置变更：作用域随目标标识带出，可见性取租户级——租户设置的变更租户要看得见。
        // 宿主级设置的记录由写入时的租户上下文（null）自然落到宿主侧。
        context.Add(
            OperationRecordActions.SettingChanged,
            OperationRecordCategories.Configuration,
            OperationVisibility.Tenant);
#if (LocalIdentity && Email)
        // 只有宿主能发测试邮件；租户里的尝试被拒后记在租户层，租户管理员看得见自己人试过。
        context.Add(
            OperationRecordActions.SettingTestEmailSent,
            OperationRecordCategories.Configuration,
            OperationVisibility.Tenant);
#endif

        // 导出审计日志本身是安全事件：谁把历史带走了必须留痕。
        // 可见性取租户级——导出的是该租户自己的记录，租户管理员有权知道谁导走了。
        context.Add(
            OperationRecordActions.OperationRecordsExported,
            OperationRecordCategories.Data,
            OperationVisibility.Tenant,
            OperationSeverity.Notice);

#if (LocalIdentity)

        // 认证事件。OWASP 明列为必需内容，此前本项目完全没有留痕。
        // 登录成功、改密码、注册是自证类动作：主体在动作完成那一刻才被证实，目标就是本人（targetIsActor）。
        // 登录失败刻意不标——它的目标是调用方提交的用户名，未经验证，标了就成了匿名可写的"操作人"。
        context.Add(
            OperationRecordActions.AuthLoginSucceeded,
            OperationRecordCategories.Authentication,
            OperationVisibility.Actor,
            targetIsActor: true);
        context.Add(
            OperationRecordActions.AuthLoginFailed,
            OperationRecordCategories.Authentication,
            OperationVisibility.Tenant,
            OperationSeverity.Notice);
        // 账号被锁定：租户管理员要能看到"谁被锁了"，才知道该不该去解锁、是不是有人在爆破
        context.Add(
            OperationRecordActions.AuthLockedOut,
            OperationRecordCategories.Authentication,
            OperationVisibility.Tenant,
            OperationSeverity.Notice);
        context.Add(
            OperationRecordActions.UserPasswordReset,
            OperationRecordCategories.Account,
            OperationVisibility.Tenant,
            OperationSeverity.Notice);
        context.Add(
            OperationRecordActions.UserUnlocked,
            OperationRecordCategories.Account,
            OperationVisibility.Tenant,
            OperationSeverity.Notice);
        context.Add(
            OperationRecordActions.UserTwoFactorReset,
            OperationRecordCategories.Account,
            OperationVisibility.Tenant,
            OperationSeverity.Notice);
        // 登出刻意不记：控制器上是 [AllowAnonymous] 的幂等 SignOut，注释写明"未登录调用同样
        // 返回成功，不泄漏这个会话存不存在"。记它等于再开一个匿名写入面，而登出不改变
        // 任何能力边界，信息量几乎为零。不留一个永不被写入的码。
        context.Add(
            OperationRecordActions.AuthPasswordChanged,
            OperationRecordCategories.Authentication,
            OperationVisibility.Actor,
            OperationSeverity.Notice,
            targetIsActor: true);
#if (Email)
        // 忘记口令：请求是匿名的，"谁"由目标承载（与注册同一写法）。租户管理员要看得到，
        // 才能对上"我没找回过口令"的报告
        context.Add(
            OperationRecordActions.AuthPasswordRecovered,
            OperationRecordCategories.Authentication,
            OperationVisibility.Tenant,
            OperationSeverity.Notice,
            targetIsActor: true);
#endif
        // 本人资料上的这几件事，只本人（与上层）可见，与改密码同一口径。
        context.Add(
            OperationRecordActions.AuthAvatarChanged,
            OperationRecordCategories.Account,
            OperationVisibility.Actor);
        // 邮箱是安全提醒的去处，也是按邮箱关联外部登录的依据：按认证类、提示级别记，与改密码同一口径
        context.Add(
            OperationRecordActions.AuthEmailChanged,
            OperationRecordCategories.Authentication,
            OperationVisibility.Actor,
            OperationSeverity.Notice,
            targetIsActor: true);
#if (Email)
        context.Add(
            OperationRecordActions.AuthEmailVerified,
            OperationRecordCategories.Authentication,
            OperationVisibility.Actor);
#endif
        context.Add(
            OperationRecordActions.AuthSessionRevoked,
            OperationRecordCategories.Authentication,
            OperationVisibility.Actor,
            OperationSeverity.Notice);
        context.Add(
            OperationRecordActions.AuthOtherSessionsRevoked,
            OperationRecordCategories.Authentication,
            OperationVisibility.Actor,
            OperationSeverity.Notice);
        context.Add(
            OperationRecordActions.AuthTwoFactorEnabled,
            OperationRecordCategories.Authentication,
            OperationVisibility.Actor,
            OperationSeverity.Notice);
        context.Add(
            OperationRecordActions.AuthTwoFactorAuthenticatorReplaced,
            OperationRecordCategories.Authentication,
            OperationVisibility.Actor,
            OperationSeverity.Notice);
        context.Add(
            OperationRecordActions.AuthTwoFactorDisabled,
            OperationRecordCategories.Authentication,
            OperationVisibility.Actor,
            OperationSeverity.Notice);
        context.Add(
            OperationRecordActions.AuthTwoFactorRecoveryCodesRegenerated,
            OperationRecordCategories.Authentication,
            OperationVisibility.Actor);
        // 用恢复码登录时还没有主体，记录的操作人为空；租户可见，才有人看得到
        context.Add(
            OperationRecordActions.AuthTwoFactorRecoveryCodeUsed,
            OperationRecordCategories.Authentication,
            OperationVisibility.Tenant,
            OperationSeverity.Notice);
        // 登录方式的增减：多一把通行密钥等于多开一扇门
        context.Add(
            OperationRecordActions.AuthTwoFactorPasskeyAdded,
            OperationRecordCategories.Authentication,
            OperationVisibility.Actor,
            OperationSeverity.Notice);
        context.Add(
            OperationRecordActions.AuthTwoFactorPasskeyRemoved,
            OperationRecordCategories.Authentication,
            OperationVisibility.Actor,
            OperationSeverity.Notice);
        context.Add(
            OperationRecordActions.AuthTwoFactorPasskeyRenamed,
            OperationRecordCategories.Authentication,
            OperationVisibility.Actor);
        context.Add(
            OperationRecordActions.AuthTwoFactorPreferredMethodChanged,
            OperationRecordCategories.Authentication,
            OperationVisibility.Actor);
#if (Email)
        context.Add(
            OperationRecordActions.AuthTwoFactorEmailEnabled,
            OperationRecordCategories.Authentication,
            OperationVisibility.Actor,
            OperationSeverity.Notice);
        context.Add(
            OperationRecordActions.AuthTwoFactorEmailDisabled,
            OperationRecordCategories.Authentication,
            OperationVisibility.Actor,
            OperationSeverity.Notice);
#endif
#if (ExternalLogin)
        // 登录方式的增减：绑定一个外部账号等于多开一扇门
        context.Add(
            OperationRecordActions.AuthExternalLoginLinked,
            OperationRecordCategories.Authentication,
            OperationVisibility.Actor,
            OperationSeverity.Notice);
        context.Add(
            OperationRecordActions.AuthExternalLoginUnlinked,
            OperationRecordCategories.Authentication,
            OperationVisibility.Actor,
            OperationSeverity.Notice);
        // 只经外部登录的账号添上口令这一扇门：与改密码同一口径
        context.Add(
            OperationRecordActions.AuthPasswordSet,
            OperationRecordCategories.Authentication,
            OperationVisibility.Actor,
            OperationSeverity.Notice,
            targetIsActor: true);
#endif
        context.Add(
            OperationRecordActions.AuthRegistered,
            OperationRecordCategories.Authentication,
            OperationVisibility.Tenant,
            targetIsActor: true);

        // 模拟登录：Tenant 可见是刻意的，见常量上的说明。Critical——它改变的是"谁在操作"。
        context.Add(
            OperationRecordActions.ImpersonationStarted,
            OperationRecordCategories.Authentication,
            OperationVisibility.Tenant,
            OperationSeverity.Critical);
        context.Add(
            OperationRecordActions.ImpersonationEnded,
            OperationRecordCategories.Authentication,
            OperationVisibility.Tenant,
            OperationSeverity.Notice);

        // 同一事件在宿主侧的那一条：目标是租户，归"租户"类，可见性 Host——
        // 它写的是宿主的人去了哪家，对任何一个租户都不该可见。
        context.Add(
            OperationRecordActions.TenantImpersonationStarted,
            OperationRecordCategories.Tenant,
            OperationVisibility.Host,
            OperationSeverity.Critical);
        context.Add(
            OperationRecordActions.TenantImpersonationEnded,
            OperationRecordCategories.Tenant,
            OperationVisibility.Host,
            OperationSeverity.Notice);
#endif
#if (OpenIddictServer)

        context.Add(
            OperationRecordActions.AuthTokenIssued,
            OperationRecordCategories.Authentication,
            OperationVisibility.Actor);
#endif
    }
}
