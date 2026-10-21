import { APP_BASE_HREF, Location } from '@angular/common';
import { MOCK_PLATFORM_LOCATION_CONFIG } from '@angular/common/testing';
import { inject, provideAppInitializer, provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
//#if (LocalIdentity)
import { Router, Routes, provideRouter, withHashLocation } from '@angular/router';
//#else
import { Router, Routes, provideRouter } from '@angular/router';
//#endif

import { EntryRouteService } from './entry-route-service';

/**
 * 「当前在哪条路由上」的读法：断言地址栏（由 `MOCK_PLATFORM_LOCATION_CONFIG` 给出），覆盖路径与
 * 哈希两种策略，并在应用初始化器里读一次（那时 `Router.url` 一律是 `/`）。
 */
describe('EntryRouteService', () => {
  function configure(
    url: string,
    options: { hash?: boolean; baseHref?: string; onInit?: () => void; routes?: Routes } = {},
  ): void {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        {
          provide: MOCK_PLATFORM_LOCATION_CONFIG,
          useValue: { startUrl: `http://localhost${url}` },
        },
        { provide: APP_BASE_HREF, useValue: options.baseHref ?? '/' },
        //#if (LocalIdentity)
        provideRouter(options.routes ?? [], ...(options.hash ? [withHashLocation()] : [])),
        //#else
        provideRouter(options.routes ?? []),
        //#endif
        ...(options.onInit ? [provideAppInitializer(options.onInit)] : []),
      ],
    });
  }

  function openAt(url: string, options: { hash?: boolean; baseHref?: string } = {}) {
    configure(url, options);
    return TestBed.inject(EntryRouteService);
  }

  describe('with path routing', () => {
    it('reads a deep link inside an app initializer, before the initial navigation', () => {
      const seen: { entry?: string; router?: string } = {};
      configure('/platform/users?page=2', {
        onInit: () => {
          seen.entry = inject(EntryRouteService).url();
          seen.router = inject(Router).url;
        },
      });
      TestBed.inject(EntryRouteService);

      // Router.url 这时还是 '/'：拿它记落地地址，重新登录后回到的是首页而不是这一页。
      expect(seen).toEqual({ entry: '/platform/users?page=2', router: '/' });
    });

    it('keeps the query string in the url and drops it from the path', () => {
      const entry = openAt('/platform/users?page=2');

      expect(entry.url()).toBe('/platform/users?page=2');
      expect(entry.path()).toBe('/platform/users');
    });

    it('strips the base href', () => {
      const entry = openAt('/app/platform/users?page=2', { baseHref: '/app/' });

      expect(entry.url()).toBe('/platform/users?page=2');
    });

    it('reports the site root as a rooted url', () => {
      expect(openAt('/').url()).toBe('/');
    });

    it('keeps the query string on the site root', () => {
      expect(openAt('/?page=2').url()).toBe('/?page=2');
    });

    it('sees the OIDC callback before the router has navigated', () => {
      // 启动阶段 Router.url 就是 '/'：只看它的话，回调页上的 401 会清掉刚建立的主体。
      expect(openAt('/auth/callback?code=abc&state=xyz').isOnAuthRoute()).toBe(true);
    });

    it('reports an ordinary route as not authenticating', () => {
      expect(openAt('/platform/users').isOnAuthRoute()).toBe(false);
    });

    it('ignores a plain anchor', () => {
      const entry = openAt('/platform/users#/auth/callback');

      expect(entry.url()).toBe('/platform/users');
      expect(entry.isOnAuthRoute()).toBe(false);
    });
  });

  /** 启动失败时守卫扣下导航目标：入口以目标为准，地址栏被 Router 还原后放回目标。 */
  describe('with a held target', () => {
    it('reports the held target instead of the address bar until it is taken', () => {
      const entry = openAt('/');

      entry.hold('/platform/users?page=2#list');

      expect(entry.url()).toBe('/platform/users?page=2#list');
      expect(entry.path()).toBe('/platform/users');
      expect(entry.takeHeld()).toBe('/platform/users?page=2#list');
      expect(entry.url()).toBe('/');
      expect(entry.takeHeld()).toBeNull();
    });

    it('puts the held target back into the address bar after the router cancels the navigation', async () => {
      const target = '/platform/users?page=2';
      configure(target, {
        routes: [{ path: 'platform/users', canActivate: [() => false], children: [] }],
      });
      const entry = TestBed.inject(EntryRouteService);
      const router = TestBed.inject(Router);
      const location = TestBed.inject(Location);

      // 不扣目标时，取消首次导航让 Router 把地址栏还原成 /
      await expect(router.navigateByUrl(target)).resolves.toBe(false);
      expect(location.path()).toBe('');

      location.replaceState(target);
      entry.hold(target);
      await expect(router.navigateByUrl(target)).resolves.toBe(false);
      expect(location.path()).toBe(target);
    });
  });
  //#if (LocalIdentity)

  // 哈希路由只在本地身份形态下可选
  describe('with hash routing', () => {
    it('reads a deep link inside an app initializer, before the initial navigation', () => {
      let seen: string | undefined;
      configure('/#/platform/users?page=2', {
        hash: true,
        onInit: () => {
          seen = inject(EntryRouteService).url();
        },
      });
      TestBed.inject(EntryRouteService);

      expect(seen).toBe('/platform/users?page=2');
    });

    it('sees the OIDC callback', () => {
      expect(openAt('/#/auth/callback?code=abc', { hash: true }).isOnAuthRoute()).toBe(true);
    });

    // 查询串或锚点里出现认证路径，人并不在那条路由上。按整条 URL 做 includes 会误判，
    // 而误判的代价是普通会话过期走进认证流程专用的处置分支。
    it('does not mistake an auth path inside the query string for an auth route', () => {
      const entry = openAt('/#/workspace?returnUrl=/auth/callback', { hash: true });

      expect(entry.path()).toBe('/workspace');
      expect(entry.isOnAuthRoute()).toBe(false);
    });

    it('does not mistake a hash without a leading slash for an auth route', () => {
      expect(openAt('/#section/auth/callback', { hash: true }).isOnAuthRoute()).toBe(false);
    });
  });
  //#endif
});
