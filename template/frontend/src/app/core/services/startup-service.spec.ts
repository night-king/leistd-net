import { Location } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';

import { AuthService } from './auth-service';
import { SessionContextService } from './session-context-service';
import { StartupService } from './startup-service';
import { ApplicationHttpError } from '../errors/application-http-error';
import { EntryRouteService } from '../routing/entry-route-service';

import type { Mock, MockedObject } from 'vitest';

describe('StartupService', () => {
  let authService: Pick<MockedObject<AuthService>, 'initializeAuth'>;
  // 会话上下文整个桩掉（它有独立单测），本组只关心状态机分支；用真实实现要连带打桩其依赖，
  // 漏掉的真实请求还会被降级逻辑吞掉而照样变绿。
  let sessionContext: Pick<MockedObject<SessionContextService>, 'establish' | 'clear'>;
  let service: StartupService;
  let isProtectedRoute: Mock;

  beforeEach(() => {
    // 认证数据的清理由会话上下文统一负责（它有独立单测），这里只需要认证探测本身。
    authService = {
      initializeAuth: vi.fn().mockName('AuthService.initializeAuth'),
    };
    sessionContext = {
      establish: vi.fn().mockName('SessionContextService.establish'),
      clear: vi.fn().mockName('SessionContextService.clear'),
    };
    sessionContext.establish.mockResolvedValue();
    TestBed.configureTestingModule({
      providers: [
        StartupService,
        provideRouter([{ path: 'platform/users', children: [] }]),
        { provide: AuthService, useValue: authService },
        { provide: SessionContextService, useValue: sessionContext },
      ],
    });
    service = TestBed.inject(StartupService);
    // 固定为受保护路由，覆盖认证探测的全部状态转换分支；判据本身由末尾两条用例
    // 用真实实现验证（mockRestore 还原真实实现 + openAt）。
    isProtectedRoute = vi
      .spyOn(
        service as unknown as {
          isProtectedRoute(): boolean;
        },
        'isProtectedRoute',
      )
      .mockReturnValue(true);
  });

  /** 设定地址栏但不导航：启动流跑在初始导航之前，读到的只能是地址栏。 */
  function openAt(url: string): void {
    TestBed.inject(Location).replaceState(url);
  }

  function httpError(status: number): ApplicationHttpError {
    return ApplicationHttpError.from(
      new HttpErrorResponse({ status, statusText: `HTTP ${status}` }),
    );
  }

  it('treats 401 as signed-out and still reaches success', async () => {
    authService.initializeAuth.mockRejectedValue(httpError(401));

    await service.load();

    // 401 视为未登录：认证数据、权限、设置由统一清理入口一起清掉，
    // 否则上一位用户的裁剪结论和偏好会留给下一个人（SessionContextService 有独立单测覆盖三样）。
    expect(sessionContext.clear).toHaveBeenCalled();
    expect(service.status()).toBe('success');
  });

  it('marks startup as failed when the auth service is unavailable (503)', async () => {
    const error = httpError(503);
    authService.initializeAuth.mockRejectedValue(error);

    await service.load();

    expect(service.status()).toBe('failed');
    expect(service.error()).toBe(error);
    // 非 401 故障不是「未登录」：不能清会话，否则一次接口抖动就把用户踢下线。
    expect(sessionContext.clear).not.toHaveBeenCalled();
  });

  it('marks startup as failed on network errors (status 0)', async () => {
    authService.initializeAuth.mockRejectedValue(httpError(0));

    await service.load();

    expect(service.status()).toBe('failed');
  });

  it('reaches success when the session probe resolves', async () => {
    authService.initializeAuth.mockResolvedValue();

    await service.load();

    // 权限与当前用户在同一次启动中就位，Guard 与菜单才不会闪现受保护入口。
    expect(sessionContext.establish).toHaveBeenCalled();
    expect(service.status()).toBe('success');
    expect(service.error()).toBeNull();
  });

  /** 守卫的共同前置：启动失败既不是未登录也不是无权限，导航被扣下，等重试。 */
  describe('settled', () => {
    it('waits while loading and lets the guard decide after success', async () => {
      authService.initializeAuth.mockReturnValue(new Promise(() => undefined));
      void service.load();

      let decided: boolean | undefined;
      const subscription = service.settled('/platform/users').subscribe((v) => (decided = v));
      await Promise.resolve();
      TestBed.tick();
      expect(decided).toBeUndefined();
      subscription.unsubscribe();

      authService.initializeAuth.mockResolvedValue();
      await service.load();

      await expect(firstValueFrom(service.settled('/platform/users'))).resolves.toBe(true);
      expect(TestBed.inject(EntryRouteService).takeHeld()).toBeNull();
    });

    it('holds the target url and rejects the navigation when startup failed', async () => {
      authService.initializeAuth.mockRejectedValue(httpError(403));
      await service.load();

      await expect(firstValueFrom(service.settled('/platform/users?offset=20#list'))).resolves.toBe(
        false,
      );
      expect(TestBed.inject(EntryRouteService).url()).toBe('/platform/users?offset=20#list');
    });
  });

  describe('retry', () => {
    it('lands on the held target once startup succeeds, without reading the address bar', async () => {
      isProtectedRoute.mockRestore();
      openAt('/platform/users?offset=20');
      authService.initializeAuth.mockRejectedValue(httpError(503));
      await service.load();
      await firstValueFrom(service.settled('/platform/users?offset=20#list'));
      // 守卫取消首次导航后 Router 会把地址栏还原成 /
      openAt('/');

      authService.initializeAuth.mockResolvedValue();
      await service.retry();

      // 入口按扣下的目标判定，仍是受保护路由：探测了会话，而不是按公开页直接放行
      expect(authService.initializeAuth).toHaveBeenCalledTimes(2);
      expect(service.status()).toBe('success');
      const router = TestBed.inject(Router);
      expect(router.url).toBe('/platform/users?offset=20#list');
      expect(TestBed.inject(Location).path(true)).toBe('/platform/users?offset=20#list');
      expect(TestBed.inject(EntryRouteService).takeHeld()).toBeNull();
    });

    it('keeps the held target while startup still fails', async () => {
      authService.initializeAuth.mockRejectedValue(httpError(503));
      await service.load();
      await firstValueFrom(service.settled('/platform/users'));

      await service.retry();

      expect(service.status()).toBe('failed');
      expect(TestBed.inject(Router).navigated).toBe(false);
      expect(TestBed.inject(EntryRouteService).url()).toBe('/platform/users');
    });
  });

  /** 入口路由判据用真实实现跑：上面的用例把它打桩成 true，判据坏掉不会变红。 */
  describe('entry route gate', () => {
    it('establishes the subject on a protected route', async () => {
      isProtectedRoute.mockRestore();
      authService.initializeAuth.mockResolvedValue();
      openAt('/platform/users');

      await service.load();

      expect(authService.initializeAuth).toHaveBeenCalled();
      expect(sessionContext.establish).toHaveBeenCalled();
    });

    it('does not probe the session on a public route', async () => {
      isProtectedRoute.mockRestore();
      authService.initializeAuth.mockResolvedValue();
      openAt('/');

      await service.load();

      expect(authService.initializeAuth).not.toHaveBeenCalled();
      expect(service.status()).toBe('success');
    });
  });
  //#if (ExternalLogin)

  /**
   * 外部登录回调页：提供商带着 code 跳回来，这一刻还没有会话，回调组件自己去换。
   * 启动流程若没认出它，就会先探一次会话（受保护判据打桩成 true 时即可观察到），
   * 把回调当普通页面处理，外部登录断在这一步且不报错。
   */
  it('lets the external sign-in callback run without probing the session', async () => {
    openAt('/auth/external-callback/github?code=abc&state=xyz');

    await service.load();

    expect(authService.initializeAuth).not.toHaveBeenCalled();
    expect(service.status()).toBe('success');
  });
  //#endif
  //#if (RemoteTokenAuth)

  /**
   * OIDC 回调页上的 401 是 API 拒了刚换到的令牌，不是未登录；按未登录处理会在浏览器与 IdP
   * 之间循环授权。
   */
  describe('on the OIDC callback', () => {
    // 回调判据落在路径上：查询串里含 `/auth/callback` 的普通页面会话过期时应按已登出处理。
    it('does not mistake an auth path inside the query string for the callback', async () => {
      isProtectedRoute.mockRestore();
      openAt('/workspace?returnUrl=/auth/callback');
      authService.initializeAuth.mockResolvedValue();
      sessionContext.establish.mockRejectedValue(httpError(401));

      await service.load();

      expect(service.status()).toBe('success');
      expect(sessionContext.clear).toHaveBeenCalled();
    });

    it('still treats a 401 outside the callback as signed out', async () => {
      isProtectedRoute.mockRestore();
      openAt('/platform/users');
      authService.initializeAuth.mockResolvedValue();
      sessionContext.establish.mockRejectedValue(httpError(401));

      await service.load();

      // 会话过期是常态，不能因为回调页那条特例把普通页面也变成故障页。
      expect(service.status()).toBe('success');
      expect(sessionContext.clear).toHaveBeenCalled();
    });
  });
  //#endif
});
