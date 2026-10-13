/**
 * 功能权限名常量。
 *
 * 必须与后端 `PermissionConstant` 逐字一致：路由 Guard、菜单、按钮裁剪和后端
 * `[Authorize(Policy = ...)]` 使用同一组值，任何一处漂移都会造成"能看见但调不通"。
 *
 * 前端裁剪只影响体验，不构成安全边界——服务端对每个请求仍会独立校验。
 */
export const PERMISSIONS = {
  /**
   * 平台仪表盘的统计数据；没有时仪表盘只显示问候与快捷入口。它只给已能进入平台的岗位附加统计，
   * 不是平台入口权限：单独持有它进来只能看到一个空的工作组。
   */
  dashboard: {
    default: 'App.Dashboard',
  },
  users: {
    default: 'App.Users',
    create: 'App.Users.Create',
    update: 'App.Users.Update',
    delete: 'App.Users.Delete',
    manageRoles: 'App.Users.ManageRoles',
  },
  roles: {
    default: 'App.Roles',
    create: 'App.Roles.Create',
    update: 'App.Roles.Update',
    delete: 'App.Roles.Delete',
    managePermissions: 'App.Roles.ManagePermissions',
  },
  //#if (LocalIdentity && IncludeMultiTenancy)
  tenants: {
    default: 'App.Tenants',
    create: 'App.Tenants.Create',
    update: 'App.Tenants.Update',
    delete: 'App.Tenants.Delete',
//#if (Impersonation)
    impersonation: 'App.Tenants.Impersonation',
//#endif
  },
  //#endif
  //#if (OpenIddictServer)
  openApplications: {
    default: 'App.OpenApplications',
    create: 'App.OpenApplications.Create',
    update: 'App.OpenApplications.Update',
    delete: 'App.OpenApplications.Delete',
    resetSecret: 'App.OpenApplications.ResetSecret',
  },
  //#endif
  settings: {
    default: 'App.Settings',
  },
//#if (IncludeOperationRecords)
  operationRecords: {
    default: 'App.OperationRecords',
    export: 'App.OperationRecords.Export',
  },
//#endif
} as const;

/**
 * 进入 `/platform` 所需的权限集，任一命中即放行。`/platform` 父路由的 `data.permissions` 与
 * `AuthorizationService.canAccessPlatform` 都引用这里，不得各自硬编码；新增平台模块时只改这里
 * （`authorization-service.spec.ts` 与后端契约测试核对同源与覆盖）。
 */
export const PLATFORM_ENTRY_PERMISSIONS = [
  // 只持有仪表盘权限也能进平台看统计：仪表盘本身就是平台首页
  PERMISSIONS.dashboard.default,
  PERMISSIONS.users.default,
  PERMISSIONS.roles.default,
  //#if (LocalIdentity && IncludeMultiTenancy)
  PERMISSIONS.tenants.default,
  //#endif
  //#if (OpenIddictServer)
  PERMISSIONS.openApplications.default,
  //#endif
  // 只持有审计查看权限的岗位（安全、合规）也要进得来：漏了这一项，
  // 那个角色的账号菜单里看不到入口、登录后还会被重定向走。
//#if (IncludeOperationRecords)
  PERMISSIONS.operationRecords.default,
//#endif
  PERMISSIONS.settings.default,
] as const;
