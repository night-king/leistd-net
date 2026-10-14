import { Location } from '@angular/common';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Route, Router, Routes } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';

import { routes } from './app.routes';
import { authGuard } from './core/guards/auth-guard';
import { forbiddenGuard } from './core/guards/permission-guard';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from './core/i18n/transloco.testing';
//#endif
import { PROTECTED_ROUTE_PREFIXES } from './core/services/startup-service';
import { NotFound } from './features/public/components/not-found/not-found';
import { PLATFORM_ENTRY_PERMISSIONS } from './shared/constants/permission.constants';

describe('top-level routes', () => {
  const isCatchAllPrefix = (r: Route) => r.path === '' && !!r.loadChildren;
  const isWildcard = (r: Route) => r.path === '**';

  // 扫到的路由数为零的话，下面两条都是空的。
  it('finds a non-zero number of routes', () => {
    expect(routes.length).toBeGreaterThan(3);
  });

  it('places the empty-path + loadChildren public area after all concrete paths', () => {
    const catchAllIndexes = (routes as Routes)
      .map((r, i) => (isCatchAllPrefix(r) ? i : -1))
      .filter((i) => i >= 0);
    expect(
      catchAllIndexes.length,
      'No public area with an empty path and loadChildren was found; this assertion would be vacuous',
    ).toBe(1);

    const catchAllAt = catchAllIndexes[0];
    const concreteAfter = (routes as Routes)
      .slice(catchAllAt + 1)
      .filter((r) => !isWildcard(r) && r.path !== '')
      .map((r) => r.path);

    expect(
      concreteAfter,
      `These concrete paths come after the empty-path public area and would be swallowed by it: ${concreteAfter.join(', ')}`,
    ).toEqual([]);
  });

  // 刷新与前进/后退还原出的 403 页靠它送回目标重新判定；两条相邻的 403 历史只差查询参数，
  // 要按查询参数变化重新执行守卫（行为见 permission-guard.spec，那里的路由声明与此一致）
  it('re-runs forbiddenGuard on the forbidden page whenever its target changes', () => {
    const forbidden = (routes as Routes).find((r) => r.path === 'forbidden');

    expect(forbidden?.canActivate).toContain(forbiddenGuard);
    expect(forbidden?.runGuardsAndResolvers).toBe('paramsOrQueryParamsChange');
  });

  it('maps PROTECTED_ROUTE_PREFIXES one-to-one to top-level authGuard routes', () => {
    const authGuarded = (routes as Routes)
      .filter((r) => r.canActivate?.includes(authGuard))
      .map((r) => `/${r.path}`)
      .sort();

    expect(
      authGuarded.length,
      'No top-level route uses authGuard; this assertion would be vacuous',
    ).toBeGreaterThan(0);

    expect(
      [...PROTECTED_ROUTE_PREFIXES].sort() as string[],
      `Routes using authGuard in the route table: ${authGuarded.join(', ')}`,
    ).toEqual(authGuarded);
  });

  it('ends with a wildcard that renders the not-found page in place instead of redirecting', () => {
    const wildcards = (routes as Routes).filter(isWildcard);

    expect(wildcards).toHaveLength(1);
    expect(routes.at(-1)).toBe(wildcards[0]);
    expect(
      wildcards[0].redirectTo,
      'Unknown URLs must not silently redirect to the home page',
    ).toBeUndefined();
    expect(wildcards[0].loadComponent).toBeDefined();
  });

  // 真实路由表：未知地址越过空路径公开区落到通配路由，地址栏保留原地址
  it('shows the not-found page for an unknown url without changing the address', async () => {
    TestBed.configureTestingModule({
      // prettier-ignore
      providers: [
        provideRouter(routes),
        //#if (IncludeLocalization)
        ...provideTranslocoTesting(['en']),
        //#endif
      ],
    });
    const harness = await RouterTestingHarness.create();

    // 第二个参数断言落地组件的类型，不是它就抛错
    await harness.navigateByUrl('/no-such-page?from=mail', NotFound);

    expect(TestBed.inject(Router).url).toBe('/no-such-page?from=mail');
    expect(TestBed.inject(Location).path()).toBe('/no-such-page?from=mail');
  });

  /** 路由守卫与菜单、重定向必须用同一份权限清单；断言同源，防止有人在路由里手写补项。 */
  it('shares the /platform route allowlist with canAccessPlatform', () => {
    const platformRoute = routes.find((route) => route.path === 'platform');

    expect(platformRoute, 'The /platform route does not exist').toBeDefined();
    expect(platformRoute?.data?.['permissions']).toEqual([...PLATFORM_ENTRY_PERMISSIONS]);
  });
});
