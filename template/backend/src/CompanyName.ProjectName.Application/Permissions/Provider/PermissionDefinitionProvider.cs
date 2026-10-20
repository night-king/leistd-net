using Leistd.Authorization.Definitions;
using Leistd.MultiTenancy.Tenancy;

namespace CompanyName.ProjectName.Application.Permissions.Provider;

/// <summary>权限定义提供器。</summary>
/// <remarks>
/// 权限管理界面与授予存储均由这里的定义驱动。组仅用于模块展示与批量操作；
/// 资源权限同时表示读取该资源，子权限表示可执行的动作。
/// 动作权限以资源权限为祖先，写入授予时会自动补齐该祖先。
/// </remarks>
public class PermissionDefinitionProvider : IPermissionDefinitionProvider
{
    // 常规动作的默认文案统一取这三个值，与页面按钮一致；中文等译文由契约测试要求同样统一
    // （新建 / 编辑 / 删除），避免各模块各写一套「新增 / 修改」。
    internal const string CreateText = "Create";
    internal const string EditText = "Edit";
    internal const string DeleteText = "Delete";
#if (!LocalIdentity)
    internal const string ToggleActivationText = "Enable or disable";
#endif

    public void Define(IPermissionDefinitionContext context)
    {
        // displayName 是默认文案（英文），不启用本地化时直接展示；译文按约定键
        // Permission:{权限名}、PermissionGroup:{分组名} 在请求阶段查找（资源类型见 Program 的 PermissionManagementOptions）。
        // 分组、顺序与前端导航一致：分组对应菜单分组，根权限对应菜单项，子权限对应页面操作。

        // 工作：仪表盘。汇总的数据都带 TenantId 并受全局查询过滤器分区，宿主与租户各看各的，因此侧别是 Both
        var workGroup = context.GetOrAddGroup(PermissionConstant.Groups.Work, displayName: "Work");
        workGroup.AddPermission(
            PermissionConstant.Dashboard.Default,
            MultiTenancySides.Both,
            displayName: "Dashboard"
        );

        // 身份与访问：谁能进来——用户、角色与租户
        var identityGroup = context.GetOrAddGroup(PermissionConstant.Groups.Identity, displayName: "Identity & access");

        var usersPermission = identityGroup.AddPermission(
            PermissionConstant.Users.Default,
            MultiTenancySides.Both,
            displayName: "User management"
        );
#if (LocalIdentity)
        usersPermission.AddChild(PermissionConstant.Users.Create, displayName: CreateText);
        usersPermission.AddChild(PermissionConstant.Users.Update, displayName: EditText);
        usersPermission.AddChild(PermissionConstant.Users.Delete, displayName: DeleteText);
#else
        // 资源服务形态下用户由签发方的令牌投影而来：本服务没有新建与删除的端点，
        // 定义了也只是给出一项勾了不起作用的权限。剩下的写操作只有启停，显示名照实说——
        // 叫"编辑"会让管理员以为能改资料，而资料归签发方所有。
        usersPermission.AddChild(PermissionConstant.Users.Update, displayName: ToggleActivationText);
#endif
        usersPermission.AddChild(PermissionConstant.Users.ManageRoles, displayName: "Assign roles");

        var rolesPermission = identityGroup.AddPermission(
            PermissionConstant.Roles.Default,
            MultiTenancySides.Both,
            displayName: "Role management"
        );
        rolesPermission.AddChild(PermissionConstant.Roles.Create, displayName: CreateText);
        rolesPermission.AddChild(PermissionConstant.Roles.Update, displayName: EditText);
        rolesPermission.AddChild(PermissionConstant.Roles.Delete, displayName: DeleteText);
        rolesPermission.AddChild(PermissionConstant.Roles.ManagePermissions, displayName: "Configure permissions");

#if (LocalIdentity && IncludeMultiTenancy)
        // 宿主侧专属：租户上下文内不可见、不可授予（子权限继承父级侧别）
        var tenantsPermission = identityGroup.AddPermission(
            PermissionConstant.Tenants.Default,
            MultiTenancySides.Host,
            displayName: "Tenant management"
        );
        tenantsPermission.AddChild(PermissionConstant.Tenants.Create, displayName: CreateText);
        tenantsPermission.AddChild(PermissionConstant.Tenants.Update, displayName: EditText);
        tenantsPermission.AddChild(PermissionConstant.Tenants.Delete, displayName: DeleteText);
#if (Impersonation)
        tenantsPermission.AddChild(PermissionConstant.Tenants.Impersonation, displayName: "Sign in as tenant");
#endif

#endif
#if (OpenIddictServer)
        // 开放应用是宿主全局资源：OpenIddict 的四张表都没有 TenantId，也就不是 IMultiTenant，
        // 全局租户过滤器对它们不生效；OpenIddictDbContext 还固定连宿主控制库、不跟随租户路由。
        // 因此侧别必须是 Host——设为 Both 会让租户管理员拿到这组权限，
        // 进而读写全系统的 OAuth 客户端（重置密钥即可让该客户端对所有租户失效）。
        var developerGroup = context.GetOrAddGroup(PermissionConstant.Groups.Developer, displayName: "Developer");
        var openApplicationsPermission = developerGroup.AddPermission(
            PermissionConstant.OpenApplications.Default,
            MultiTenancySides.Host,
            displayName: "Open applications"
        );
        openApplicationsPermission.AddChild(PermissionConstant.OpenApplications.Create, displayName: CreateText);
        openApplicationsPermission.AddChild(PermissionConstant.OpenApplications.Update, displayName: EditText);
        openApplicationsPermission.AddChild(PermissionConstant.OpenApplications.Delete, displayName: DeleteText);
        openApplicationsPermission.AddChild(PermissionConstant.OpenApplications.ResetSecret, displayName: "Reset client secret");

#endif
#if (IncludeOperationRecords)
        // 审计：记录带 TenantId 并受全局查询过滤器分区，宿主与租户各看各的，因此侧别是 Both
        var auditGroup = context.GetOrAddGroup(PermissionConstant.Groups.Audit, displayName: "Audit");
        var operationRecordsPermission = auditGroup.AddPermission(
            PermissionConstant.OperationRecords.Default,
            MultiTenancySides.Both,
            displayName: "Operation records"
        );
        // 导出与查看分开：导出把审计数据整批带离系统，之后不再受可见性分层约束、也不再有访问记录。
        operationRecordsPermission.AddChild(PermissionConstant.OperationRecords.Export, displayName: "Export");

#endif
        // 系统：父权限是系统设置页的入口，每个分组一条子权限。
        // 显示、注册、登录与会话是组织各自的设置，侧别 Both；其余分组只有进程级设置，侧别 Host，租户管理员拿不到。
        var systemGroup = context.GetOrAddGroup(PermissionConstant.Groups.System, displayName: "System");
        var settingsPermission = systemGroup.AddPermission(
            PermissionConstant.Settings.Default,
            MultiTenancySides.Both,
            displayName: "System settings"
        );
        settingsPermission.AddChild(PermissionConstant.Settings.Display, displayName: "Display");
#if (LocalIdentity)
        settingsPermission.AddChild(PermissionConstant.Settings.Registration, displayName: "Registration");
        settingsPermission.AddChild(PermissionConstant.Settings.Security, displayName: "Sign-in & sessions");
#if (ExternalLogin)
        settingsPermission.AddChild(PermissionConstant.Settings.ExternalLogin, displayName: "External sign-in", side: MultiTenancySides.Host);
#endif
#if (Email)
        settingsPermission.AddChild(PermissionConstant.Settings.Email, displayName: "Email", side: MultiTenancySides.Host);
#endif
#endif
        settingsPermission.AddChild(PermissionConstant.Settings.Operations, displayName: "Operations", side: MultiTenancySides.Host);
#if (IncludeOperationRecords || IncludeNotifications)
        settingsPermission.AddChild(PermissionConstant.Settings.Retention, displayName: "Data retention", side: MultiTenancySides.Host);
#endif
    }
}
