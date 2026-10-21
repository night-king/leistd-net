import { Location } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import {
  ActivatedRouteSnapshot,
  provideRouter,
  Router,
  RouterStateSnapshot,
  UrlTree,
} from '@angular/router';
import { firstValueFrom, isObservable, of } from 'rxjs';

import { authGuard } from './auth-guard';
import { User } from '../../shared/models/user.model';
import { ApplicationHttpError } from '../errors/application-http-error';
import { EntryRouteService } from '../routing/entry-route-service';
import { AuthService } from '../services/auth-service';
//#if (Impersonation)
import { ImpersonationService } from '../services/impersonation-service';
//#endif
import { SessionContextService } from '../services/session-context-service';
import { StartupService } from '../services/startup-service';

describe('authGuard', () => {
  const currentUser = signal<User | null>(null);
  const startLogin = vi.fn();
  const initializeAuth = vi.fn<() => Promise<void>>();
  const establish = vi.fn<() => Promise<void>>();

  beforeEach(async () => {
    currentUser.set(null);
    startLogin.mockReset();
    initializeAuth.mockResolvedValue();
    establish.mockResolvedValue();
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        {
          provide: AuthService,
          useValue: {
            isAuthenticated: () => currentUser() !== null,
            currentUser,
            initializeAuth,
            startLogin,
          },
        },
        // 启动流用真实实现，只桩掉它调用的会话上下文（有独立单测）
        { provide: SessionContextService, useValue: { establish, clear: vi.fn() } },
        //#if (Impersonation)
        { provide: ImpersonationService, useValue: { load: vi.fn() } },
        //#endif
      ],
    });
  });

  /** 从受保护的深链跑一次启动流；`failure` 给出时探测会话以该错误失败。 */
  async function startUp(failure?: unknown): Promise<void> {
    TestBed.inject(Location).replaceState('/workspace');
    if (failure) {
      initializeAuth.mockRejectedValue(failure);
    }
    await TestBed.inject(StartupService).load();
  }

  async function activate(url: string) {
    const result = TestBed.runInInjectionContext(() =>
      authGuard({} as ActivatedRouteSnapshot, { url } as RouterStateSnapshot),
    );
    const value = await firstValueFrom(isObservable(result) ? result : of(result));
    return value instanceof UrlTree ? TestBed.inject(Router).serializeUrl(value) : value;
  }

  function signIn(user: Partial<User> = {}): void {
    currentUser.set(new User({ id: 'u1', username: 'alice', roles: [], ...user }));
  }

  function httpError(status: number): ApplicationHttpError {
    return ApplicationHttpError.from(
      new HttpErrorResponse({ status, statusText: `HTTP ${status}` }),
    );
  }

  it('lets an authenticated user through', async () => {
    await startUp();
    signIn();

    await expect(activate('/workspace')).resolves.toBe(true);
  });
  //#if (LocalIdentity)

  it('sends an anonymous visitor to the login page with the requested url as returnUrl', async () => {
    await startUp(httpError(401));

    await expect(activate('/platform/users?offset=20')).resolves.toBe(
      '/auth/login?returnUrl=%2Fplatform%2Fusers%3Foffset%3D20',
    );
  });

  // 受限会话只能去两步验证设置页，并带上这次要去的地方，设置完成后回到那里
  it('confines a session that must set up two-factor authentication to the setup page', async () => {
    await startUp();
    signIn({ twoFactorSetupRequired: true });

    await expect(activate('/platform/users?offset=20')).resolves.toBe(
      '/auth/two-factor-setup?returnUrl=%2Fplatform%2Fusers%3Foffset%3D20',
    );
  });
  //#else

  it('starts the login flow for an anonymous visitor and blocks the navigation', async () => {
    await startUp(httpError(401));

    await expect(activate('/platform/users')).resolves.toBe(false);
    expect(startLogin).toHaveBeenCalledWith('/platform/users');
  });
  //#endif

  /**
   * 启动失败不是未登录：跳登录在资源服务形态会与签发方静默往返成死循环，在本地身份形态会把故障
   * 说成"请登录"。导航被拦下、目标被扣下，页面停在启动失败卡片上。
   */
  for (const status of [403, 503, 0]) {
    it(`blocks without a login redirect when startup failed with status ${status}`, async () => {
      await startUp(httpError(status));

      await expect(activate('/platform/users?offset=20')).resolves.toBe(false);
      expect(startLogin).not.toHaveBeenCalled();
      expect(TestBed.inject(EntryRouteService).url()).toBe('/platform/users?offset=20');
    });
  }

  // 认证成功、后续加载失败时主体还在，也不能放行
  it('blocks a signed-in user when startup failed after authentication', async () => {
    signIn();
    establish.mockRejectedValue(httpError(500));
    await startUp();

    expect(TestBed.inject(StartupService).status()).toBe('failed');
    await expect(activate('/workspace')).resolves.toBe(false);
  });
});
