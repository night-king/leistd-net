import { Routes } from '@angular/router';
//#if (IncludeLocalization)
import { provideTranslocoScope } from '@jsverse/transloco';
//#endif

import { authGuard } from './core/guards/auth-guard';
import { forbiddenGuard, permissionGuard } from './core/guards/permission-guard';
//#if (IncludeLocalization)
import { resolveTranslationScopes } from './core/i18n/translation-scopes';
//#endif
import { DefaultLayout } from './layout/default/default-layout';
//#if (LocalIdentity)
import { EmptyLayout } from './layout/empty/empty-layout';
//#endif
import { WorkspaceLayout } from './layout/workspace/workspace-layout';
import { PLATFORM_ENTRY_PERMISSIONS } from './shared/constants/permission.constants';

export const routes: Routes = [
  //#if (LocalIdentity)
  {
    path: 'auth',
    //#if (IncludeLocalization)
    providers: [provideTranslocoScope('account')],
    resolve: { translations: resolveTranslationScopes },
    //#endif
    component: EmptyLayout,
    loadChildren: () => import('./features/account/account.routes').then((r) => r.AUTH_ROUTES),
  },
  //#endif
  //#if (RemoteTokenAuth)
  {
    path: 'auth/login',
    loadComponent: () =>
      import('./core/components/resource-login/resource-login').then((m) => m.ResourceLogin),
  },
  //#endif
  // 工作空间：面向业务用户，顶栏导航。入口多了需要分组时换回 DefaultLayout（见 frontend-ui.md「导航与菜单分组」）
  {
    path: 'workspace',
    component: WorkspaceLayout,
    canActivate: [authGuard],
    loadChildren: () =>
      import('./features/workspace/workspace.routes').then((r) => r.WORKSPACE_ROUTES),
  },
  // 已登录但无权限：与 401 的登录跳转区分开，避免"登录成功又被弹回登录页"的循环。
  // permissionGuard 跳到这里时地址栏显示被拒的目标；刷新与前进/后退由 forbiddenGuard 送回目标重新判定。
  {
    path: 'forbidden',
    canActivate: [forbiddenGuard],
    // 内部地址按被拒的目标区分（?target=），两条无权限历史之间前进/后退也要重新执行入口守卫
    runGuardsAndResolvers: 'paramsOrQueryParamsChange',
    //#if (IncludeLocalization)
    providers: [provideTranslocoScope('forbidden')],
    resolve: { translations: resolveTranslationScopes },
    //#endif
    loadComponent: () =>
      import('./features/public/components/forbidden/forbidden').then((m) => m.Forbidden),
  },

  {
    path: 'platform',
    component: DefaultLayout,
    // 按权限放行，不按角色名。各子路由再声明各自所需的权限。
    canActivate: [authGuard, permissionGuard],
    data: {
      // 引用单一来源，不要在这里手写清单；缘由见 PLATFORM_ENTRY_PERMISSIONS 的注释
      permissions: [...PLATFORM_ENTRY_PERMISSIONS],
    },
    loadChildren: () =>
      import('./features/platform/platform.routes').then((r) => r.PLATFORM_ROUTES),
  },

  // 空路径按前缀匹配，必须位于所有具体路由之后。
  {
    path: '',
    loadChildren: () => import('./features/public/public.routes').then((r) => r.PUBLIC_ROUTES),
  },

  // 未知地址原地显示 404 页，不改写地址栏，也不悄悄送回首页。
  {
    path: '**',
    //#if (IncludeLocalization)
    providers: [provideTranslocoScope('notFound')],
    resolve: { translations: resolveTranslationScopes },
    //#endif
    loadComponent: () =>
      import('./features/public/components/not-found/not-found').then((m) => m.NotFound),
  },
];
