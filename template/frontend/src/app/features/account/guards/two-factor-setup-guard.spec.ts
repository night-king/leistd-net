import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import {
  ActivatedRouteSnapshot,
  convertToParamMap,
  provideRouter,
  Router,
  RouterStateSnapshot,
  UrlTree,
} from '@angular/router';
import { Observable, of, throwError } from 'rxjs';

import { twoFactorSetupGuard } from './two-factor-setup-guard';
import { authGuard } from '../../../core/guards/auth-guard';
//#if (OpenIddictServer)
import { ReturnUrlService } from '../../../core/routing/return-url-service';
//#endif
import { AuthService } from '../../../core/services/auth-service';
import { AuthorizationService } from '../../../core/services/authorization-service';
import { SessionContextService } from '../../../core/services/session-context-service';
import { StartupService } from '../../../core/services/startup-service';
import { User } from '../../../shared/models/user.model';

describe('twoFactorSetupGuard', () => {
  const currentUser = signal<User | null>(null);
  let loadUser: () => Observable<unknown>;
  const establish = vi.fn<() => Promise<void>>();
  const canAccessPlatform = vi.fn<() => boolean>();

  beforeEach(() => {
    currentUser.set(null);
    loadUser = vi.fn(() => throwError(() => new Error('401')));
    establish.mockReset().mockResolvedValue();
    canAccessPlatform.mockReset().mockReturnValue(false);
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        {
          provide: AuthService,
          useValue: {
            isAuthenticated: () => currentUser() !== null,
            currentUser,
            loadUser: () => loadUser(),
          },
        },
        { provide: AuthorizationService, useValue: { canAccessPlatform } },
        { provide: SessionContextService, useValue: { establish } },
      ],
    });
  });

  async function activate(returnUrl?: string): Promise<string | boolean> {
    const route = {
      queryParamMap: convertToParamMap(returnUrl === undefined ? {} : { returnUrl }),
    } as ActivatedRouteSnapshot;
    const result = await TestBed.runInInjectionContext(() =>
      twoFactorSetupGuard(route, {} as RouterStateSnapshot),
    );
    return result instanceof UrlTree
      ? TestBed.inject(Router).serializeUrl(result)
      : (result as boolean);
  }

  /** 服务端此刻的判定：守卫每次进入都重新读当前用户，读到的就是它。 */
  function serverSays(twoFactorSetupRequired: boolean): void {
    loadUser = vi.fn(() => {
      currentUser.set(new User({ id: 'u1', username: 'alice', roles: [], twoFactorSetupRequired }));
      return of(undefined);
    });
  }

  it('lets a session that must set up two-factor authentication open the page', async () => {
    serverSays(true);

    expect(await activate('/platform/users')).toBe(true);
    expect(establish).not.toHaveBeenCalled();
  });

  // 内存里还是"受限"，服务端已解除（要求刚被关闭）：以服务端为准，不停在设置页
  it('re-reads the current user on every entry instead of trusting the cached one', async () => {
    currentUser.set(
      new User({ id: 'u1', username: 'alice', roles: [], twoFactorSetupRequired: true }),
    );
    serverSays(false);

    expect(await activate('/platform/users?offset=20')).toBe('/platform/users?offset=20');
    expect(loadUser).toHaveBeenCalledTimes(1);
    expect(establish).toHaveBeenCalledTimes(1);
  });

  // 反方向：内存里是"不受限"，服务端已要求设置（拦截器把人送来时）：留在设置页
  it('keeps a session the server now restricts even if the cached user was unrestricted', async () => {
    currentUser.set(
      new User({ id: 'u1', username: 'alice', roles: [], twoFactorSetupRequired: false }),
    );
    serverSays(true);

    expect(await activate('/workspace')).toBe(true);
  });

  it('sends an unrestricted session without a usable target to its default page', async () => {
    serverSays(false);
    canAccessPlatform.mockReturnValue(true);

    expect(await activate()).toBe('/platform');
  });

  for (const unusable of [
    'https://evil.example/x',
    '//evil.example/x',
    '/auth/two-factor-setup?returnUrl=%2Fworkspace',
    '',
  ]) {
    it(`falls back to the default page instead of the unusable target "${unusable}"`, async () => {
      serverSays(false);

      expect(await activate(unusable)).toBe('/workspace');
    });
  }
  //#if (OpenIddictServer)

  // 授权端点不是前端路由：整页回到服务端，原样带回缓存的授权请求
  it('returns to an authorization request with a full page load', async () => {
    serverSays(false);
    const loadPage = vi
      .spyOn(TestBed.inject(ReturnUrlService), 'loadPage')
      .mockImplementation(() => undefined);
    const target =
      '/connect/authorize?client_id=resource&request_uri=urn%3Aietf%3Aparams%3Aoauth%3Arequest_uri%3Aabc';

    expect(await activate(target)).toBe(false);
    expect(loadPage).toHaveBeenCalledWith(target);
  });
  //#endif

  it('sends a visitor without a session to the login page and keeps the target', async () => {
    expect(await activate('/platform/users')).toBe('/auth/login?returnUrl=%2Fplatform%2Fusers');
  });

  // 与应用守卫串起来走真实路由：要求打开时停在设置页并记着原目标，要求关闭后回到原目标，两页之间不来回跳
  describe('together with the auth guard', () => {
    @Component({ template: '' })
    class Blank {}

    let router: Router;

    beforeEach(() => {
      TestBed.overrideProvider(StartupService, { useValue: { settled: () => of(true) } });
      router = TestBed.inject(Router);
      router.resetConfig([
        { path: 'platform/users', canActivate: [authGuard], component: Blank },
        { path: 'workspace', canActivate: [authGuard], component: Blank },
        { path: 'auth/two-factor-setup', canActivate: [twoFactorSetupGuard], component: Blank },
      ]);
    });

    it('keeps a restricted session on the setup page and remembers where it was heading', async () => {
      serverSays(true);
      currentUser.set(
        new User({ id: 'u1', username: 'alice', roles: [], twoFactorSetupRequired: true }),
      );

      await router.navigateByUrl('/platform/users');

      expect(router.url).toBe('/auth/two-factor-setup?returnUrl=%2Fplatform%2Fusers');
    });

    // 组织关闭了要求，内存里的当前用户仍是"受限"：应用守卫把人送到设置页，设置页守卫读到不受限，
    // 回到原目标，应用守卫这次放行——走一趟就停，不在两页之间来回跳
    it('lets the session through once the requirement is off, without bouncing back', async () => {
      serverSays(false);
      currentUser.set(
        new User({ id: 'u1', username: 'alice', roles: [], twoFactorSetupRequired: true }),
      );

      await router.navigateByUrl('/platform/users');

      expect(router.url).toBe('/platform/users');
      expect(loadUser).toHaveBeenCalledTimes(1);
    });
  });
});
