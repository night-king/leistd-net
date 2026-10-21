import { Routes } from '@angular/router';
//#if (IncludeLocalization)
import { provideTranslocoScope } from '@jsverse/transloco';
//#endif

import { permissionGuard } from '../../core/guards/permission-guard';
//#if (IncludeLocalization)
import { resolveTranslationScopes } from '../../core/i18n/translation-scopes';
//#endif
import { PERMISSIONS } from '../../shared/constants/permission.constants';

/** 平台管理模块路由，作为 Default Layout 的子路由。 */
export const PLATFORM_ROUTES: Routes = [
  {
    path: '',
    //#if (IncludeLocalization)
    // 裁剪后只剩一项时 prettier 会要求折成一行；固定书写形态
    // prettier-ignore
    providers: [
      //#if (IncludeOperationRecords)
      // 最近操作的句子与记录详情的词条在 operationRecords scope
      provideTranslocoScope('platform', 'operationRecords'),
      //#else
      provideTranslocoScope('platform'),
      //#endif
    ],
    resolve: { translations: resolveTranslationScopes },
    //#endif
    loadComponent: () => import('./components/dashboard/dashboard').then((m) => m.Dashboard),
  },
  {
    path: 'users',
    //#if (IncludeLocalization)
    providers: [provideTranslocoScope('users')],
    resolve: { translations: resolveTranslationScopes },
    //#endif
    loadComponent: () => import('./components/users/users').then((m) => m.Users),
    canActivate: [permissionGuard],
    data: { permission: PERMISSIONS.users.default },
  },
  {
    path: 'roles',
    //#if (IncludeLocalization)
    providers: [provideTranslocoScope('roles', 'permissions')],
    resolve: { translations: resolveTranslationScopes },
    //#endif
    loadComponent: () => import('./components/roles/roles').then((m) => m.Roles),
    canActivate: [permissionGuard],
    data: { permission: PERMISSIONS.roles.default },
  },
  {
    // 系统设置：一个后端设置分组就是一个面板（:group 是分组标识的短横线写法），空路径由外壳导向第一个面板。
    path: 'settings',
    //#if (IncludeLocalization)
    providers: [provideTranslocoScope('settings')],
    resolve: { translations: resolveTranslationScopes },
    //#endif
    loadComponent: () =>
      import('../settings/components/system-settings/system-settings').then(
        (m) => m.SystemSettings,
      ),
    canActivate: [permissionGuard],
    data: { permission: PERMISSIONS.settings.default },
    children: [
      { path: '', children: [] },
      {
        path: ':group',
        loadComponent: () =>
          import('../settings/components/setting-section/setting-section').then(
            (m) => m.SettingSection,
          ),
        data: { scope: 'system' },
      },
    ],
  },
//#if (IncludeOperationRecords)
  {
    // 审计：谁在什么时候做了什么。只读，无写端点。
    path: 'operation-records',
    //#if (IncludeLocalization)
    providers: [provideTranslocoScope('operationRecords')],
    resolve: { translations: resolveTranslationScopes },
    //#endif
    loadComponent: () =>
      import('./components/operation-records/operation-records').then((m) => m.OperationRecords),
    canActivate: [permissionGuard],
    data: { permission: PERMISSIONS.operationRecords.default },
  },
//#endif
  //#if (LocalIdentity && IncludeMultiTenancy)
  {
    path: 'tenants',
    //#if (IncludeLocalization)
    providers: [provideTranslocoScope('tenants')],
    resolve: { translations: resolveTranslationScopes },
    //#endif
    loadComponent: () => import('./components/tenants/tenants').then((m) => m.Tenants),
    canActivate: [permissionGuard],
    data: { permission: PERMISSIONS.tenants.default },
  },
  //#endif
  //#if (OpenIddictServer)
  {
    path: 'open-applications',
    //#if (IncludeLocalization)
    providers: [provideTranslocoScope('openApp')],
    resolve: { translations: resolveTranslationScopes },
    //#endif
    loadComponent: () =>
      import('./components/open-applications/open-applications').then((m) => m.OpenApplications),
    canActivate: [permissionGuard],
    data: { permission: PERMISSIONS.openApplications.default },
  },
  //#endif
];
