namespace CompanyName.ProjectName.Application.Permissions.Provider;

/// <summary>权限定义。</summary>
/// <remarks>
/// 全部权限统一使用 <c>App.</c> 前缀。这些常量既是权限定义的名称，也是
/// <c>[Authorize(Policy = ...)]</c> 的策略名和前端裁剪使用的契约，三处必须是同一组值。
/// </remarks>
public static class PermissionConstant
{
    /// <summary>权限名前缀。</summary>
    public const string Prefix = "App";

    /// <summary>权限分组的标识符。</summary>
    /// <remarks>
    /// <b>分组与前端菜单分组按约定对应</b>：根权限对应菜单项，子权限对应页面上的操作按钮——
    /// 管理员在权限配置里看到的结构，就是用户在界面上看到的结构。
    /// <b>这个对应没有自动校验</b>：前端 <c>MenuGroup</c> 没有 id 字段，它的 <c>label</c> 是词条键，
    /// 与这里的值不在同一个取值空间；后端契约测试也不读前端源码（前后端测试各自独立）。
    /// 因此改一边要手工同步另一边。
    /// 分组不是权限，因此不与任何 <c>App.*</c> 同名；分组名不落库（授予只存权限名），可自由调整。
    /// </remarks>
    public static class Groups
    {
        /// <summary>工作：仪表盘。</summary>
        public const string Work = "Work";

        /// <summary>谁能进来：用户、角色与租户。</summary>
        public const string Identity = "Identity";
#if (OpenIddictServer)

        /// <summary>代表第三方接入的开放应用。</summary>
        public const string Developer = "Developer";
#endif

        /// <summary>审计：操作记录。</summary>
#if (IncludeOperationRecords)
        public const string Audit = "Audit";
#endif

        /// <summary>系统：系统设置。</summary>
        public const string System = "System";
    }

    /// <summary>仪表盘权限。</summary>
    /// <remarks>
    /// 只约束"看"，因此是一个扁平权限：仪表盘只读汇总，没有别的动作可授。侧别为 <c>Both</c>——
    /// 汇总的数据（用户、登录记录）带 <c>TenantId</c> 且受全局查询过滤器分区，宿主看宿主的、租户看自己的。
    /// </remarks>
    public static class Dashboard
    {
        /// <summary>查看平台统计。</summary>
        public const string Default = Prefix + ".Dashboard";
    }

    /// <summary>用户管理权限。</summary>
    public static class Users
    {
        public const string Default = Prefix + ".Users";
        public const string Create = Default + ".Create";
        public const string Update = Default + ".Update";
        public const string Delete = Default + ".Delete";

        /// <summary>分配用户角色。与普通资料更新分离，避免持有编辑权限即可提权。</summary>
        public const string ManageRoles = Default + ".ManageRoles";
    }

    /// <summary>角色管理权限。</summary>
    public static class Roles
    {
        public const string Default = Prefix + ".Roles";
        public const string Create = Default + ".Create";
        public const string Update = Default + ".Update";
        public const string Delete = Default + ".Delete";
        public const string ManagePermissions = Default + ".ManagePermissions";
    }

#if (OpenIddictServer)
    /// <summary>开放应用（OAuth2 客户端）管理权限（宿主侧专属）。</summary>
    /// <remarks>
    /// 开放应用持有 ClientId/ClientSecret，能代表本系统对外颁发令牌，
    /// 因此与用户、角色同级独立成权限族，而不是复用用户管理权限。
    /// <para>定义时声明 Host 侧别：OpenIddict 的表没有 TenantId，不是 <c>IMultiTenant</c>，
    /// 全局租户过滤器对它们不生效，因此这是一组<b>宿主全局</b>资源。设为 Both 会让
    /// 租户管理员拿到它，进而读写全系统的 OAuth 客户端。</para>
    /// </remarks>
    public static class OpenApplications
    {
        public const string Default = Prefix + ".OpenApplications";
        public const string Create = Default + ".Create";
        public const string Update = Default + ".Update";
        public const string Delete = Default + ".Delete";

        /// <summary>重置客户端密钥。等价于换发凭据，与普通编辑分开授权。</summary>
        public const string ResetSecret = Default + ".ResetSecret";
    }

#endif
#if (LocalIdentity && IncludeMultiTenancy)
    /// <summary>租户管理权限（宿主侧专属）。</summary>
    /// <remarks>
    /// 定义时声明 Host 侧别：租户上下文内对任何主体（含租户管理员）不可见、不可授予、检查一律拒绝，
    /// 租户管理员不会在权限树里看到"管理租户"这类平台能力。
    /// </remarks>
    public static class Tenants
    {
        public const string Default = Prefix + ".Tenants";
        public const string Create = Default + ".Create";
        public const string Update = Default + ".Update";
        public const string Delete = Default + ".Delete";
#if (Impersonation)

        /// <summary>以租户管理员身份登录该租户（模拟登录）。</summary>
        /// <remarks>
        /// 与 <see cref="Update"/> 分开授权：改租户的注册信息和"进到租户里面去操作"
        /// 是两种不同量级的能力，后者能看到并改动该租户的全部业务数据。
        /// </remarks>
        public const string Impersonation = Default + ".Impersonation";
#endif
    }
#endif
    /// <summary>设置管理权限。</summary>
    /// <remarks>
    /// <para>父权限是系统设置页的入口；每个系统设置分组一条子权限，管理（读、写）该分组的租户层或进程级值。
    /// 分组与子权限的对应见 <c>SettingGroupPermissions</c>。</para>
    /// <para>个人偏好是个人数据，读写不需要权限。</para>
    /// </remarks>
    public static class Settings
    {
        /// <summary>进入系统设置页。</summary>
        public const string Default = Prefix + ".Settings";

        /// <summary>显示：组织默认的界面语言与时区。</summary>
        public const string Display = Default + ".Display";

#if (LocalIdentity)
        /// <summary>注册：是否开放自助注册、验证方式与验证码时效。</summary>
        public const string Registration = Default + ".Registration";

        /// <summary>登录与会话：登录锁定、两步验证与会话策略。</summary>
        public const string Security = Default + ".Security";

#if (ExternalLogin)
        /// <summary>外部登录：第三方登录开关与客户端凭据（进程级）。</summary>
        public const string ExternalLogin = Default + ".ExternalLogin";

#endif
#if (Email)
        /// <summary>邮件发送：SMTP 参数与发送测试邮件（进程级）。</summary>
        public const string Email = Default + ".Email";

#endif
#endif
        /// <summary>运维：日志级别等进程怎么跑的开关（进程级）。</summary>
        public const string Operations = Default + ".Operations";
#if (IncludeOperationRecords || IncludeNotifications)

        /// <summary>数据保留：过期操作记录与通知的清理（进程级）。</summary>
        public const string Retention = Default + ".Retention";
#endif
    }

#if (IncludeOperationRecords)
    /// <summary>操作记录查看权限。</summary>
    /// <remarks>
    /// 只约束"看"，因此是一个扁平权限而非"资源 + 动作"：记录写下就不再修改或删除，
    /// 没有别的动作可授。侧别为 <c>Both</c>——记录带 <c>TenantId</c> 且受全局查询过滤器分区，
    /// 宿主看宿主的、租户看自己的，不需要靠侧别再分一次。
    /// </remarks>
    public static class OperationRecords
    {
        /// <summary>查看操作记录。</summary>
        public const string Default = Prefix + ".OperationRecords";

        /// <summary>导出操作记录。</summary>
        /// <remarks>
        /// <b>与查看分开授权</b>：导出把审计数据整批带离系统，之后既不受本系统的可见性分层约束，
        /// 也不再有访问记录——影响面与在线翻页查看不是一个量级。GitHub、Salesforce 等
        /// 同样把导出单列一项权限。导出动作本身也会被审计（<c>operation-records.exported</c>）。
        /// </remarks>
        public const string Export = Default + ".Export";
    }
#endif
}
