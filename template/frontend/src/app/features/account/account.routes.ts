import { Routes } from '@angular/router';

import { twoFactorSetupGuard } from './guards/two-factor-setup-guard';

/** 认证模块路由，作为 Empty Layout 的子路由。 */
export const AUTH_ROUTES: Routes = [
  {
    path: 'login',
    loadComponent: () => import('./components/login/login').then((m) => m.Login),
  },
  {
    // 组织要求两步验证而本人尚未启用：受限会话只能到这里
    path: 'two-factor-setup',
    canActivate: [twoFactorSetupGuard],
    loadComponent: () =>
      import('./components/two-factor-required/two-factor-required').then(
        (m) => m.TwoFactorRequired,
      ),
  },
  //#if (OpenIddictServer)
  {
    // 依赖方发起的退出、且无法确认是当前会话时，由用户确认
    path: 'logout-confirm',
    loadComponent: () =>
      import('./components/logout-confirm/logout-confirm').then((m) => m.LogoutConfirm),
  },
  //#endif
  {
    path: 'forgot-password',
    loadComponent: () =>
      import('./components/forgot-password/forgot-password').then((m) => m.ForgotPassword),
  },
  {
    path: 'register',
    loadComponent: () => import('./components/register/register').then((m) => m.Register),
  },
  //#if (ExternalLogin)
  {
    // 提供商在路径里：回调地址是一条不带查询串的固定路径，提供商后台按原样登记即可
    path: 'external-callback/:provider',
    loadComponent: () =>
      import('./components/external-auth-callback/external-auth-callback').then(
        (m) => m.ExternalAuthCallback,
      ),
  },
  //#endif
  {
    path: '',
    redirectTo: 'login',
    pathMatch: 'full',
  },
];

export default AUTH_ROUTES;
