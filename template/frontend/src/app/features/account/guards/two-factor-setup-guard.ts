import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { lastValueFrom } from 'rxjs';

import { ReturnUrlService } from '../../../core/routing/return-url-service';
import { AuthService } from '../../../core/services/auth-service';
import { AuthorizationService } from '../../../core/services/authorization-service';
import { SessionContextService } from '../../../core/services/session-context-service';

/**
 * 强制两步验证设置页只给受限会话用。受限与否随组织策略与本人状态即时变化，内存里的当前用户可能已经过时，
 * 所以每次进入都重新读一次：未登录回登录页；不受限（例如要求刚被关闭）就建立会话上下文，回到原目标或默认页，
 * 不在设置页与应用之间来回跳。
 */
export const twoFactorSetupGuard: CanActivateFn = async (route) => {
  const authService = inject(AuthService);
  const authorizationService = inject(AuthorizationService);
  const sessionContext = inject(SessionContextService);
  const returnUrls = inject(ReturnUrlService);
  const router = inject(Router);
  const target = returnUrls.resolve(route.queryParamMap.get('returnUrl'));

  try {
    await lastValueFrom(authService.loadUser());
  } catch {
    return router.createUrlTree(['/auth/login'], {
      queryParams: target ? { returnUrl: target } : {},
    });
  }

  if (authService.currentUser()?.twoFactorSetupRequired) {
    return true;
  }

  // 原目标可能在需要权限的区域：先把权限与设置就位，SPA 内跳转不会重跑应用初始化器
  await sessionContext.establish();
  if (!target) {
    return router.createUrlTree([
      authorizationService.canAccessPlatform() ? '/platform' : '/workspace',
    ]);
  }
  //#if (OpenIddictServer)
  if (target.startsWith('/connect/')) {
    returnUrls.loadPage(target);
    return false;
  }
  //#endif
  return router.parseUrl(target);
};
