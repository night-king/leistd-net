import { inject } from '@angular/core';
import { CanActivateFn, RedirectCommand, Router } from '@angular/router';
import { map } from 'rxjs/operators';

import { AuthorizationService } from '../services/authorization-service';
import { StartupService } from '../services/startup-service';

/** 标记"这次导航是 {@link permissionGuard} 刚拒绝的"。放在导航的 `info` 里：它只属于这一次导航，不进历史记录。 */
const PERMISSION_DENIED = Symbol('permission-denied');

/** 无权限页的内部地址带上被拒的目标：每个目标各有一个内部地址，相邻两条无权限的历史也彼此不同。 */
const FORBIDDEN_TARGET = 'target';

/**
 * 按功能权限放行路由：`data.permission`（单个）或 `data.permissions`（任一即可），未声明时只要求已认证；
 * 不满足时显示无权限页，地址栏显示被拒的目标 URL；刷新与前进/后退回到这一条时由 {@link forbiddenGuard}
 * 送回目标重新判定。启动失败时先由 {@link StartupService.settled} 拦下，不拿空权限下结论。只读路由自身的 data，
 * 不用合并了父路由的 `ActivatedRouteSnapshot.data`，否则 `/platform` 的宽松声明会盖住子路由更严格的要求。
 */
export const permissionGuard: CanActivateFn = (route, state) => {
  const authorizationService = inject(AuthorizationService);
  const router = inject(Router);

  return inject(StartupService)
    .settled(state.url)
    .pipe(
      map((started) => {
        if (!started) {
          return false;
        }

        const ownData = route.routeConfig?.data ?? {};
        const required: string[] = ownData['permissions'] ?? [];
        const single: string | undefined = ownData['permission'];
        const permissions = single ? [...required, single] : required;

        if (permissions.length === 0 || authorizationService.hasAny(...permissions)) {
          return true;
        }

        // browserUrl 让地址栏显示被拒的目标：冷启动深链与应用内跳转都一样
        const forbidden = router.createUrlTree(['/forbidden'], {
          queryParams: { [FORBIDDEN_TARGET]: state.url },
        });
        return new RedirectCommand(forbidden, { browserUrl: state.url, info: PERMISSION_DENIED });
      }),
    );
};

/**
 * 无权限页的入口：刚被 {@link permissionGuard} 拒绝的导航与不带目标的直接访问照常显示，其余带目标的导航送回目标重新判定。
 *
 * Router 把"内部是无权限页、地址栏是目标"记在历史条目里：刷新与前进/后退回到这一条时直接还原出无权限页，
 * 不经过目标路由的守卫，其间获得的权限就不会生效。还原出来的导航没有拒绝标记（`info` 不进历史），据此识别。
 * 内部地址按目标区分，配合路由的 `runGuardsAndResolvers: 'paramsOrQueryParamsChange'`，在两条无权限的历史之间
 * 前进/后退也会重新执行本守卫。送回时清掉 `browserUrl` 与 `info`：仍被拒就由 permissionGuard 带着新标记跳回来，
 * 到此为止，不会循环；历史条目随之替换，不新增。
 */
export const forbiddenGuard: CanActivateFn = (route) => {
  const router = inject(Router);
  const target = route.queryParamMap.get(FORBIDDEN_TARGET);

  if (!target || router.currentNavigation()?.extras.info === PERMISSION_DENIED) {
    return true;
  }

  return new RedirectCommand(router.parseUrl(target), { browserUrl: undefined, info: undefined });
};
