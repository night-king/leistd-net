import { Routes } from '@angular/router';
//#if (IncludeLocalization)
import { provideTranslocoScope } from '@jsverse/transloco';
//#endif
//#if (IncludeLocalization)

import { resolveTranslationScopes } from '../../core/i18n/translation-scopes';
//#endif

export const WORKSPACE_ROUTES: Routes = [
  {
    path: '',
    pathMatch: 'full',
    redirectTo: 'dashboard',
  },
  {
    path: 'dashboard',
    //#if (IncludeLocalization)
    providers: [provideTranslocoScope('workspace')],
    resolve: { translations: resolveTranslationScopes },
    //#endif
    loadComponent: () =>
      import('./components/dashboard/workspace-dashboard').then((m) => m.WorkspaceDashboard),
  },
  {
    // 个人设置挂在 workspace，只要求认证（platform 父路由要求管理类权限）；面板是子路由，面板名进 URL。
    path: 'settings',
    //#if (IncludeLocalization)
    providers: [provideTranslocoScope('settings')],
    resolve: { translations: resolveTranslationScopes },
    //#endif
    loadComponent: () =>
      import('../settings/components/personal-settings/personal-settings').then(
        (m) => m.PersonalSettings,
      ),
    children: [
      //#if (LocalIdentity)
      { path: '', pathMatch: 'full', redirectTo: 'profile' },
      {
        path: 'profile',
        //#if (IncludeLocalization)
        providers: [provideTranslocoScope('account')],
        resolve: { translations: resolveTranslationScopes },
        //#endif
        loadComponent: () =>
          import('../account/components/profile-panel/profile-panel').then((m) => m.ProfilePanel),
      },
      {
        path: 'security',
        //#if (IncludeLocalization)
        providers: [provideTranslocoScope('account')],
        resolve: { translations: resolveTranslationScopes },
        //#endif
        loadComponent: () =>
          import('../account/components/security-panel/security-panel').then(
            (m) => m.SecurityPanel,
          ),
      },
      //#if (IncludeNotifications)
      {
        // 通知偏好是"类别 × 渠道"的一组开关，单独一个面板，不混进通用偏好
        path: 'notifications',
        loadComponent: () =>
          import('../settings/components/setting-section/setting-section').then(
            (m) => m.SettingSection,
          ),
        data: { scope: 'account', group: 'notifications' },
      },
      //#endif
      //#else
      { path: '', pathMatch: 'full', redirectTo: 'preferences' },
      //#endif
      {
        // 所有允许用户覆盖的设置分组都在这里，新增一项用户级设置会自动出现
        path: 'preferences',
        loadComponent: () =>
          import('../settings/components/setting-section/setting-section').then(
            (m) => m.SettingSection,
          ),
        data: { scope: 'account', exclude: ['Notifications'] },
      },
    ],
  },
  {
    path: 'placeholder',
    //#if (IncludeLocalization)
    providers: [provideTranslocoScope('workspace')],
    resolve: { translations: resolveTranslationScopes },
    //#endif
    loadComponent: () =>
      import('./components/placeholder/workspace-placeholder').then((m) => m.WorkspacePlaceholder),
  },
];
