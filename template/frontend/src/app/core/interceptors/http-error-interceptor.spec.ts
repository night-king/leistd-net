import { Location } from '@angular/common';
import {
  HttpContext,
  HttpErrorResponse,
  HttpEvent,
  HttpHandlerFn,
  HttpHeaders,
  HttpRequest,
} from '@angular/common/http';
import { Injector, provideZonelessChangeDetection, runInInjectionContext } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
//#if (IncludeLocalization)
import { TranslocoService } from '@jsverse/transloco';
//#endif
//#if (LocalIdentity)
import { Observable, of, throwError } from 'rxjs';
//#else
import { Observable, throwError } from 'rxjs';
//#endif

import { SILENT_AUTH } from './http-context-tokens';
import { httpErrorInterceptor } from './http-error-interceptor';
import { applicationErrorMessage, ApplicationHttpError } from '../errors/application-http-error';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../i18n/transloco.testing';
//#endif
import { AuthService } from '../services/auth-service';
import { SessionContextService } from '../services/session-context-service';
//#if (IncludeMultiTenancy)
import { TenantContextService } from '../services/tenant-context-service';
import { TENANT_INVALID_HEADER } from '../tenancy/tenant-protocol';
//#endif

//#if (LocalIdentity)
import type { Mock, MockedObject } from 'vitest';
//#else
import type { MockedObject } from 'vitest';
//#endif

/**
 * 直接以 runInInjectionContext 驱动拦截器：next 用 throwError 同步发射错误，
 * catchError 同步映射，因此错误在订阅时同步落到 error 回调（zoneless 无需 fakeAsync）。
 */
describe('httpErrorInterceptor', () => {
  let injector: Injector;
  let router: Router;
  //#if (LocalIdentity)
  let navigate: Mock;
  //#endif
  let sessionContext: Pick<MockedObject<SessionContextService>, 'clear'>;
  //#if (LocalIdentity)
  let authService: Pick<MockedObject<AuthService>, 'isAuthenticated' | 'loadUser'>;
  //#else
  let authService: Pick<MockedObject<AuthService>, 'isAuthenticated' | 'startLogin'>;
  //#endif

  beforeEach(() => {
    // 拦截器的契约就是「调统一清理入口」，真实实例只会连带拉起整条会话依赖链。
    sessionContext = {
      clear: vi.fn().mockName('SessionContextService.clear'),
    };
    //#if (LocalIdentity)
    authService = {
      isAuthenticated: vi.fn().mockName('AuthService.isAuthenticated'),
      loadUser: vi.fn().mockName('AuthService.loadUser'),
    };
    authService.loadUser.mockReturnValue(of(undefined) as never);
    //#else
    authService = {
      isAuthenticated: vi.fn().mockName('AuthService.isAuthenticated'),
      startLogin: vi.fn().mockName('AuthService.startLogin'),
    };
    //#endif
    authService.isAuthenticated.mockReturnValue(true);
    // 清理是同步的，清完就没有主体了——并发 401 的收敛全靠这一点，替身必须照实模拟。
    sessionContext.clear.mockImplementation(() =>
      authService.isAuthenticated.mockReturnValue(false),
    );
    TestBed.configureTestingModule({
      // prettier-ignore
      providers: [
                provideZonelessChangeDetection(),
                provideRouter([]),
                { provide: SessionContextService, useValue: sessionContext },
                { provide: AuthService, useValue: authService },
                //#if (IncludeLocalization)
                ...provideTranslocoTesting(),
                //#endif
            ],
    });
    injector = TestBed.inject(Injector);
    router = TestBed.inject(Router);
    //#if (LocalIdentity)
    navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);
    //#endif
  });

  /** 设定地址栏但不导航：Router.url 保持 '/'，对应启动流跑在初始导航之前。 */
  function openAt(url: string): void {
    TestBed.inject(Location).replaceState(url);
  }

  function runInterceptor(
    error: unknown,
    options?: {
      context?: HttpContext;
      method?: 'GET' | 'POST';
    },
  ): unknown {
    const req =
      options?.method === 'POST'
        ? new HttpRequest('POST', '/api/test', null, { context: options.context })
        : new HttpRequest('GET', '/api/test', { context: options?.context });
    const next: HttpHandlerFn = () => throwError(() => error) as Observable<HttpEvent<unknown>>;

    let caught: unknown;
    runInInjectionContext(injector, () => httpErrorInterceptor(req, next)).subscribe({
      error: (value: unknown) => (caught = value),
    });
    return caught;
  }

  function httpError(
    status: number,
    error: unknown = null,
    headers?: Record<string, string>,
  ): HttpErrorResponse {
    return new HttpErrorResponse({
      status,
      statusText: `status ${status}`,
      url: '/api/test',
      error,
      headers: headers ? new HttpHeaders(headers) : undefined,
    });
  }

  for (const status of [403, 404, 409, 422, 500]) {
    it(`normalizes a ${status} response to an ApplicationHttpError`, () => {
      const caught = runInterceptor(httpError(status));
      expect(caught).toBeInstanceOf(ApplicationHttpError);
      expect((caught as ApplicationHttpError).status).toBe(status);
    });
  }

  it('normalizes a network failure (status 0) to an ApplicationHttpError', () => {
    //#if (IncludeLocalization)
    TestBed.inject(TranslocoService).setTranslation(
      { common: { networkError: '无法连接服务器' } },
      'en',
    );
    //#endif
    const caught = runInterceptor(httpError(0, new ProgressEvent('error')));
    expect(caught).toBeInstanceOf(ApplicationHttpError);
    expect((caught as ApplicationHttpError).status).toBe(0);
    // 展示的是本地化的"连不上服务器"，不是浏览器给开发者看的原始异常文本。
    //#if (IncludeLocalization)
    expect((caught as ApplicationHttpError).message).toBe('无法连接服务器');
    //#else
    expect((caught as ApplicationHttpError).message).toBe(
      'Unable to reach the server. Check your connection and try again.',
    );
    //#endif
  });
  //#if (IncludeLocalization)

  // 错误对象带的是现成文字，事后不会随词条更新：词条未到时（首帧前的启动请求）写进去的裸键会一直留着
  it('falls back to built-in English, not bare keys, while translations have not arrived', () => {
    const caught = runInterceptor(httpError(0, new ProgressEvent('error')));

    expect((caught as ApplicationHttpError).message).toBe(
      'Unable to reach the server. Check your connection and try again.',
    );
    expect((caught as ApplicationHttpError).traceIdLabel).toBe('Trace ID');
  });
  //#endif

  it('parses an RFC 9457 errors array (code + details)', () => {
    const caught = runInterceptor(
      httpError(422, { errors: [{ code: 'name_required', detail: 'Name is required.' }] }),
    );
    const applicationError = caught as ApplicationHttpError;
    expect(applicationError).toBeInstanceOf(ApplicationHttpError);
    expect(applicationError.code).toBe('name_required');
    expect(applicationError.message).toBe('Name is required.');
    expect(applicationError.details.length).toBe(1);
  });

  it('reads the business error code from problem details', () => {
    const caught = runInterceptor(
      httpError(409, {
        code: 'Role:NameExists',
        detail: 'Role name already exists.',
        traceId: 'trace-1',
      }),
    );
    const applicationError = caught as ApplicationHttpError;
    expect(applicationError).toBeInstanceOf(ApplicationHttpError);
    expect(applicationError.status).toBe(409);
    expect(applicationError.code).toBe('Role:NameExists');
    expect(applicationError.message).toBe('Role name already exists.');
    expect(applicationError.traceId).toBe('trace-1');
  });

  it('uses the active localization label for a reportable trace ID', () => {
    //#if (IncludeLocalization)
    TestBed.inject(TranslocoService).setTranslation({ common: { traceId: '追踪号' } }, 'en');
    //#endif
    const caught = runInterceptor(httpError(503, {
        code: 'Auth:EmailVerificationUnavailable',
        detail: 'Unavailable',
        traceId: 'trace-5',
      }),);
    //#if (IncludeLocalization)
    expect(applicationErrorMessage(caught)).toBe('Unavailable (追踪号: trace-5)');
    //#else
    expect(applicationErrorMessage(caught)).toBe('Unavailable (Trace ID: trace-5)');
    //#endif
  });

  // 写请求的结果不确定：提示先核实，拦截器不自动重放
  it('marks a 5xx on a write request as uncertain using the request method', () => {
    //#if (IncludeLocalization)
    TestBed.inject(TranslocoService).setTranslation(
      { common: { uncertainResult: '操作可能已经完成，请先核实' } },
      'en',
    );
    //#endif
    const caught = runInterceptor(httpError(502, { title: 'Bad Gateway' }), { method: 'POST' });

    expect((caught as ApplicationHttpError).category).toBe('uncertain');
    //#if (IncludeLocalization)
    expect((caught as ApplicationHttpError).message).toBe('操作可能已经完成，请先核实');
    //#else
    expect((caught as ApplicationHttpError).message).toBe(
      'The operation may have completed. Check the result before trying again.',
    );
    //#endif
  });

  it('marks a 5xx without a problem on a read request as server, not the status text', () => {
    const caught = runInterceptor(httpError(502, '<html><body>Bad Gateway</body></html>'));

    expect((caught as ApplicationHttpError).category).toBe('server');
    expect((caught as ApplicationHttpError).message).not.toContain('status 502');
    expect((caught as ApplicationHttpError).message).not.toContain('Bad Gateway');
  });

  it('marks a lost write request (status 0) as uncertain', () => {
    const caught = runInterceptor(httpError(0, new ProgressEvent('error')), { method: 'POST' });

    expect((caught as ApplicationHttpError).category).toBe('uncertain');
  });

  it('lets a non-HTTP error pass through unchanged toward the GlobalErrorHandler', () => {
    const original = new Error('boom');
    const caught = runInterceptor(original);
    expect(caught).toBe(original);
    expect(caught instanceof ApplicationHttpError).toBe(false);
  });

  /**
   * 「重新发起认证」在两种身份形态下是不同的动作：本地身份跳自带登录页，
   * OIDC 形态重新走授权。断言收在这里，用例主体两种形态共用一份。
   */
  function expectReauthentication(returnUrl: string): void {
    //#if (LocalIdentity)
    expect(navigate).toHaveBeenCalledWith(['/auth/login'], { queryParams: { returnUrl } });
    //#else
    expect(authService.startLogin).toHaveBeenCalledWith(returnUrl);
    //#endif
  }

  /** 恢复只能发生一次，且带着最初那个落地地址。 */
  function expectSingleReauthentication(returnUrl: string): void {
    //#if (LocalIdentity)
    expect(vi.mocked(navigate).mock.calls).toEqual([
      [['/auth/login'], { queryParams: { returnUrl } }],
    ]);
    //#else
    expect(vi.mocked(authService.startLogin).mock.calls).toEqual([[returnUrl]]);
    //#endif
  }

  function expectNoReauthentication(): void {
    //#if (LocalIdentity)
    expect(navigate).not.toHaveBeenCalled();
    //#else
    expect(authService.startLogin).not.toHaveBeenCalled();
    //#endif
  }

  // 断言统一清理入口而非 AuthService.clearAuthData()，否则退回"只清认证数据"也能通过。
  // OIDC 形态没有静默续期，isAuthenticated() 只看内存主体，两种形态都必须在这里清理并重新认证。
  it('clears the whole session and re-initiates authentication on a non-silent 401', () => {
    const caught = runInterceptor(httpError(401));

    expect(sessionContext.clear).toHaveBeenCalled();
    expectReauthentication('/');
    expect(caught).toBeInstanceOf(ApplicationHttpError);
    expect((caught as ApplicationHttpError).status).toBe(401);
  });

  //#if (LocalIdentity)
  // 再认证连续失败的临时锁定只表示"这次操作被拒"：会话仍有效，且锁定期内登录页进不去。
  // 服务端一半由 ReauthenticationLockoutTests 守。
  it('keeps the session on a 401 that only means this attempt was refused', () => {
    const caught = runInterceptor(
      httpError(401, { code: 'Auth:UserTemporarilyLockedOut', detail: 'Too many attempts.' }),
    );

    expect(sessionContext.clear).not.toHaveBeenCalled();
    expect(navigate).not.toHaveBeenCalled();
    // 错误照常抛给页面，由它就地显示后端给的原因
    expect(caught).toBeInstanceOf(ApplicationHttpError);
    expect((caught as ApplicationHttpError).message).toBe('Too many attempts.');
  });

  // 只豁免临时锁定一个码：管理员锁定没有截止时间，那种会话本就该结束（User.AllowsExistingSessions）。
  it('still signs out on an administrator lockout', () => {
    runInterceptor(httpError(401, { code: 'Auth:UserLockedOut', detail: 'Account locked.' }));

    expect(sessionContext.clear).toHaveBeenCalled();
    expectReauthentication('/');
  });

  //#endif
  // 直接打开深链时，启动流跑在初始导航之前——那时 Router.url 是 '/'，
  // 用它记落地地址会把用户重新登录后送去首页，而不是他点开的那一页。
  it('records the deep link that has not been navigated to yet', () => {
    openAt('/platform/users?page=2');

    runInterceptor(httpError(401));

    expect(router.url).toBe('/');
    expectSingleReauthentication('/platform/users?page=2');
  });

  // 认证路由上不能再发起认证：OIDC 形态下回调页再授权会立刻带新 code 跳回，形成死循环。
  it('leaves the 401 entirely to the running auth flow while on an auth route', () => {
    openAt('/auth/callback?code=abc');

    const caught = runInterceptor(httpError(401));

    // 也不能清会话：主体刚建立，清掉后会多绕一轮授权。
    expect(sessionContext.clear).not.toHaveBeenCalled();
    expectNoReauthentication();
    // 归一化照做：调用方（这里是启动流）要靠它判断状态码。
    expect(caught).toBeInstanceOf(ApplicationHttpError);
    expect((caught as ApplicationHttpError).status).toBe(401);
  });

  // 令牌到期时一屏的并发请求会一起 401，这是常规场景。第一条恢复之后，
  // 后到的那些不能把最初的落地地址覆盖成认证页自己或默认页。
  it('recovers only once when a batch of 401s arrives', () => {
    openAt('/platform/users');

    runInterceptor(httpError(401));
    runInterceptor(httpError(401));
    runInterceptor(httpError(401));

    // 恢复跑两遍会让两条 authorize() 交叉覆盖 PKCE codeVerifier，回调换 token 失败；
    // 收敛靠第一条已同步清掉主体。
    expectSingleReauthentication('/platform/users');
    expect(sessionContext.clear).toHaveBeenCalledTimes(1);
  });

  // 本来就没有主体时，这里没有东西要清，重新认证也该由 Guard 或启动流按自己的时机发起。
  it('does nothing but normalize when there is no subject to drop', () => {
    authService.isAuthenticated.mockReturnValue(false);

    const caught = runInterceptor(httpError(401));

    expect(sessionContext.clear).not.toHaveBeenCalled();
    expectNoReauthentication();
    expect(caught).toBeInstanceOf(ApplicationHttpError);
  });
  //#if (IncludeMultiTenancy)

  // 「租户没了」与「是否重新认证」正交：认证路由上的静默请求带这个头时，租户必须清，会话与认证不动。
  it('clears an invalid tenant even on an auth route, without touching the session', () => {
    openAt('/auth/login');
    const tenantContext = TestBed.inject(TenantContextService);
    const clearTenantSpy = vi.spyOn(tenantContext, 'clear').mockReturnValue(undefined);
    const context = new HttpContext().set(SILENT_AUTH, true);

    runInterceptor(httpError(401, null, { [TENANT_INVALID_HEADER]: '1' }), { context });

    expect(clearTenantSpy).toHaveBeenCalled();
    expect(sessionContext.clear).not.toHaveBeenCalled();
    expectNoReauthentication();
  });

  it('clears the selected tenant on a 401 marked X-Tenant-Invalid', () => {
    const tenantContext = TestBed.inject(TenantContextService);
    const clearTenantSpy = vi.spyOn(tenantContext, 'clear').mockReturnValue(undefined);

    runInterceptor(httpError(401, null, { [TENANT_INVALID_HEADER]: '1' }));

    // 不清的话，重新认证后会带着这个已失效的租户再次被拒——用户换个地方卡住
    expect(clearTenantSpy).toHaveBeenCalled();
  });

  it('does not treat an ordinary 401 as an invalid tenant', () => {
    const tenantContext = TestBed.inject(TenantContextService);
    const clearTenantSpy = vi.spyOn(tenantContext, 'clear').mockReturnValue(undefined);

    runInterceptor(httpError(401));

    // 普通 401 的租户去向由 AuthService.clearAuthData() 决定（经 sessionContext.clear()，此处是替身），
    // 这里只断言拦截器没有越过那层去清。
    expect(clearTenantSpy).not.toHaveBeenCalled();
  });

  it('clears the tenant on a silent 401 marked X-Tenant-Invalid, without touching auth or routing', () => {
    const tenantContext = TestBed.inject(TenantContextService);
    const clearTenantSpy = vi.spyOn(tenantContext, 'clear').mockReturnValue(undefined);
    const context = new HttpContext().set(SILENT_AUTH, true);

    runInterceptor(httpError(401, null, { [TENANT_INVALID_HEADER]: '1' }), { context });

    // 静默只表达"别为后台请求打断用户"，与"这个租户已经没了"是两件事：
    // /auth/me 与 /permissions/current 同样会撞上失效租户，不清就留到重新认证后再次被拒
    expect(clearTenantSpy).toHaveBeenCalled();
    expect(sessionContext.clear).not.toHaveBeenCalled();
    expectNoReauthentication();
  });
  //#endif
  //#if (LocalIdentity)

  describe('a session that turned restricted (organization now requires two-factor)', () => {
    const restricted = () => httpError(403, { code: 'Auth:TwoFactorSetupRequired' });

    function navigatedUrls(): string[] {
      return vi
        .mocked(router.navigateByUrl)
        .mock.calls.map(([url]) => (typeof url === 'string' ? url : router.serializeUrl(url)));
    }

    beforeEach(() => vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true));

    // 先刷新当前用户再去设置页：守卫读它，旧的"不受限"会把人送回工作台，两页之间来回跳
    it('refreshes the current user, then goes to the setup page carrying the current page', async () => {
      openAt('/platform/users?offset=20');

      runInterceptor(restricted());
      await vi.waitFor(() => expect(router.navigateByUrl).toHaveBeenCalled());

      expect(authService.loadUser).toHaveBeenCalledOnce();
      expect(vi.mocked(authService.loadUser).mock.invocationCallOrder[0]).toBeLessThan(
        vi.mocked(router.navigateByUrl).mock.invocationCallOrder[0],
      );
      expect(navigatedUrls()).toEqual([
        '/auth/two-factor-setup?returnUrl=%2Fplatform%2Fusers%3Foffset%3D20',
      ]);
    });

    // 刷新失败也照样去设置页：那里的守卫会再读一次，由它决定去留
    it('still goes to the setup page when refreshing the current user fails', async () => {
      openAt('/workspace');
      authService.loadUser.mockReturnValue(throwError(() => httpError(500)) as never);

      runInterceptor(restricted());
      await vi.waitFor(() => expect(router.navigateByUrl).toHaveBeenCalled());

      expect(navigatedUrls()).toEqual(['/auth/two-factor-setup?returnUrl=%2Fworkspace']);
    });

    // 认证路由上（包括设置页自己）的流程自己处置，拦截器不插手
    it('leaves authentication routes alone', async () => {
      openAt('/auth/two-factor-setup?returnUrl=%2Fworkspace');

      const caught = runInterceptor(restricted());
      await Promise.resolve();

      expect(authService.loadUser).not.toHaveBeenCalled();
      expect(router.navigateByUrl).not.toHaveBeenCalled();
      expect((caught as ApplicationHttpError).code).toBe('Auth:TwoFactorSetupRequired');
    });
  });
  //#endif

  // 启动阶段的 /auth/me 与 /permissions/current 都是静默的：这里若打断，
  // 启动流自己的 401 处置就会和拦截器抢方向盘。
  it('honors SILENT_AUTH: skips the 401 recovery but still normalizes the error', () => {
    const context = new HttpContext().set(SILENT_AUTH, true);

    const caught = runInterceptor(httpError(401), { context });

    expect(sessionContext.clear).not.toHaveBeenCalled();
    expectNoReauthentication();
    expect(caught).toBeInstanceOf(ApplicationHttpError);
  });
});
