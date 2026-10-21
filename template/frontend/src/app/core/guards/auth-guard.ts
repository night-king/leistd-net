import { inject } from '@angular/core';
//#if (LocalIdentity)
import { CanActivateFn, Router } from '@angular/router';
//#else
import { CanActivateFn } from '@angular/router';
//#endif
import { map } from 'rxjs/operators';

//#if (LocalIdentity)
import { ReturnUrlService } from '../routing/return-url-service';
//#endif
import { AuthService } from '../services/auth-service';
import { StartupService } from '../services/startup-service';

/** 要求已认证；启动失败时先由 {@link StartupService.settled} 拦下，不当作未登录。 */
export const authGuard: CanActivateFn = (_route, state) => {
  const authService = inject(AuthService);
  //#if (LocalIdentity)
  const router = inject(Router);
  const returnUrls = inject(ReturnUrlService);
  //#endif

  return inject(StartupService)
    .settled(state.url)
    .pipe(
      map((started) => {
        if (!started) {
          return false;
        }

        if (authService.isAuthenticated()) {
          //#if (LocalIdentity)
          // 受限会话（组织要求两步验证而本人尚未启用）只能去设置页，完成后回到这次要去的地方；
          // 服务端同样只放行设置所需的接口
          if (authService.currentUser()?.twoFactorSetupRequired) {
            return returnUrls.twoFactorSetup(state.url);
          }
          //#endif
          return true;
        }

        //#if (RemoteTokenAuth)
        authService.startLogin(state.url);
        return false;
        //#else
        return router.createUrlTree(['/auth/login'], {
          queryParams: { returnUrl: state.url },
        });
        //#endif
      }),
    );
};
