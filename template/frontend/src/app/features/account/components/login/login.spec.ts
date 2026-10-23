import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { WritableSignal, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
//#if (IncludeLocalization)
import { provideTranslocoScope, TranslocoService } from '@jsverse/transloco';
//#endif
import { toast } from '@spartan-ng/brain/sonner';
// prettier-ignore
import {
  of, throwError,
  //#if (IncludeMultiTenancy || ExternalLogin)
  Observable,
  //#endif
} from 'rxjs';

import { Login } from './login';
import { ApplicationHttpError } from '../../../../core/errors/application-http-error';
import { permissionGuard } from '../../../../core/guards/permission-guard';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../../../core/i18n/transloco.testing';
//#endif
//#if (OpenIddictServer)
import { ReturnUrlService } from '../../../../core/routing/return-url-service';
//#endif
import { AuthService } from '../../../../core/services/auth-service';
import { AuthorizationService } from '../../../../core/services/authorization-service';
//#if (IncludeLocalization)
import { LanguageService } from '../../../../core/services/language-service';
//#endif
import { SecurityConfigService } from '../../../../core/services/security-config-service';
import { SessionContextService } from '../../../../core/services/session-context-service';
import { StartupService } from '../../../../core/services/startup-service';
//#if (IncludeMultiTenancy)
import { TenantContextService } from '../../../../core/services/tenant-context-service';
//#endif
import { PERMISSIONS } from '../../../../shared/constants/permission.constants';
//#if (ExternalLogin)
import { ExternalLoginProvidersOutputDto } from '../../dtos/account.dto';
//#endif
//#if (IncludeMultiTenancy)
import { TenantByHostOutputDto } from '../../dtos/tenant-by-host.dto';
//#endif
//#if (ExternalLogin || IncludeMultiTenancy)
import { AccountService } from '../../services/account-service';
//#endif
import { TwoFactorPrompt } from '../two-factor-challenge/two-factor-challenge';

import type { Mock, MockedObject } from 'vitest';

/**
 * 登录提交路径。returnUrl 来自查询串，只接受本地绝对路径（`//host` 与含 `://` 的一律丢弃），
 * 防止登录后被送往外站。
 */
/** 外部登录回调经导航状态带来的第二步。 */
const PROMPT: TwoFactorPrompt = {
  token: 'challenge',
  methods: ['authenticator', 'recoveryCode'],
  defaultMethod: 'authenticator',
  maskedEmail: null,
};

describe('Login', () => {
  let fixture: ComponentFixture<Login>;
  let component: Login;
  let router: Router;
  let authService: Pick<
    MockedObject<AuthService>,
    'login' | 'loadUser' | 'clearAuthData' | 'currentUser'
  >;
  let currentUser: WritableSignal<{
    twoFactorSetupRequired?: boolean;
  } | null>;
  let authorization: AuthorizationService;
  let queryParams: Record<string, string>;
  let navigationState: { twoFactorPrompt: TwoFactorPrompt; returnUrl: string } | undefined;
  //#if (IncludeMultiTenancy)
  // 主机名探测的返回值。默认"域名不表态"，与本机开发一致；租户相关用例逐个覆盖它。
  let byHost: Observable<TenantByHostOutputDto>;
  //#endif
  //#if (ExternalLogin)
  // 部署已配置的提供商。每次读取时取当前值，重试用例在两次请求之间换掉它。
  let externalProviders: Observable<ExternalLoginProvidersOutputDto>;
  //#endif

  /** @param initialize 为 false 时只构造组件、不触发 `ngOnInit`，用于核对构造期没有副作用。 */
  async function setUp(initialize = true): Promise<void> {
    currentUser = signal<{
      twoFactorSetupRequired?: boolean;
    } | null>(null);
    authService = {
      login: vi.fn().mockName('AuthService.login'),
      loadUser: vi.fn().mockName('AuthService.loadUser'),
      clearAuthData: vi.fn().mockName('AuthService.clearAuthData'),
      currentUser: currentUser as never,
    };
    // 具体载荷与本用例无关：登录流程只关心"成功/失败"和随后的跳转。
    authService.login.mockReturnValue(of(undefined) as never);
    authService.loadUser.mockReturnValue(of(undefined) as never);

    await TestBed.configureTestingModule({
      imports: [Login],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        //#if (IncludeLocalization)
        ...provideTranslocoTesting(['en', 'zh-CN']),
        provideTranslocoScope('account'),
        //#endif
        { provide: AuthService, useValue: authService },
        // 真实 permissionGuard 会先等启动流结束；登录页自身不依赖它，给个已完成的桩即可。
        { provide: StartupService, useValue: { settled: () => of(true) } },
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { queryParamMap: convertToParamMap(queryParams) } },
        },
      ],
    }).compileComponents();

    // 登录成功后会建立会话上下文（拉权限 + 拉设置）来决定落地页；不打桩的话这些真实请求
    // 永远等不到响应，用例会以超时失败，而不是报出真正的断言。
    authorization = TestBed.inject(AuthorizationService);
    vi.spyOn(TestBed.inject(SessionContextService), 'establish').mockResolvedValue();
    vi.spyOn(TestBed.inject(SessionContextService), 'clear');
    //#if (IncludeMultiTenancy)
    // 登录页构造时就会按主机名探测一次租户；不打桩的话它会挂在一个永不返回的请求上，
    // 租户区会一直停在 pending，所有与租户有关的断言都测不到真实分支。
    vi.spyOn(TestBed.inject(AccountService), 'getTenantByHost').mockImplementation(() => byHost);
    //#endif
    //#if (ExternalLogin)
    vi.spyOn(TestBed.inject(AccountService), 'getExternalLoginProviders').mockImplementation(
      () => externalProviders,
    );
    //#endif

    //#if (IncludeLocalization)
    // 与应用启动和路由解析器相同：组件创建前语言与功能词条必须已就位。
    await TestBed.inject(LanguageService).initialized;
    await TestBed.inject(LanguageService).loadScopes(['account']);
    //#endif
    if (navigationState) {
      vi.spyOn(TestBed.inject(Router), 'currentNavigation').mockReturnValue({
        extras: { state: navigationState },
      } as never);
    }
    fixture = TestBed.createComponent(Login);
    component = fixture.componentInstance;
    router = TestBed.inject(Router);

    vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);
    vi.spyOn(router, 'navigate').mockResolvedValue(true);

    if (!initialize) {
      return;
    }
    fixture.detectChanges();
    //#if (IncludeLocalization)
    // 登录页初始化时清理旧主体，等待这次退回设备语言后再测试用户切换。
    await TestBed.inject(LanguageService).resetToDeviceLang();
    //#endif
  }

  function fillValidCredentials(): void {
    component.loginForm.usernameOrEmail().value.set('admin');
    component.loginForm.password().value.set('Admin@123456');
  }

  beforeEach(() => {
    queryParams = {};
    navigationState = undefined;
    //#if (IncludeMultiTenancy)
    byHost = of({ decision: 'undecided' as const });
    localStorage.clear();
    //#endif
    //#if (ExternalLogin)
    externalProviders = of({ providers: ['github', 'google'] });
    //#endif
  });
  //#if (IncludeMultiTenancy)
  afterEach(() => localStorage.clear());
  //#endif

  // 每次进入登录页都取当前注册策略：管理员可能在上次读取之后重新开放了注册
  it('reads the registration policy again each time the page is entered', async () => {
    await setUp();
    const http = TestBed.inject(HttpTestingController);
    TestBed.tick();
    http
      .match('/api/v1/auth/security-config')
      .forEach((request) => request.flush({ registrationEnabled: false }));
    expect(TestBed.inject(SecurityConfigService).registrationOpen()).toBe(false);

    fixture.destroy();
    TestBed.createComponent(Login);
    TestBed.tick();

    expect(http.match('/api/v1/auth/security-config')).toHaveLength(1);
  });

  it('clears the previous subject on init rather than on construction, exactly once', async () => {
    await setUp(false);
    expect(TestBed.inject(SessionContextService).clear).not.toHaveBeenCalled();
    expect(authService.clearAuthData).not.toHaveBeenCalled();

    fixture.detectChanges();
    fixture.detectChanges();

    expect(TestBed.inject(SessionContextService).clear).toHaveBeenCalledOnce();
    expect(authService.clearAuthData).toHaveBeenCalledOnce();
  });

  it('keeps the existing context and displays credentials when reauthentication is required', async () => {
    queryParams = { reauthenticate: 'true' };
    await setUp();
    currentUser.set({});
    fixture.detectChanges();
    expect(TestBed.inject(SessionContextService).clear).not.toHaveBeenCalled();
    expect(
      (fixture.nativeElement as HTMLElement).querySelector('input[type="password"]'),
    ).not.toBeNull();
    fillValidCredentials();
    await component.onSubmit();
    expect(authService.login).toHaveBeenCalled();
    expect(TestBed.inject(SessionContextService).clear).toHaveBeenCalledOnce();
    expect(TestBed.inject(SessionContextService).establish).toHaveBeenCalledOnce();
  });

  it('keeps the existing context when reauthentication credentials fail', async () => {
    queryParams = { reauthenticate: 'true' };
    await setUp();
    currentUser.set({});
    authService.login.mockReturnValue(throwError(() => ({ status: 401 })));
    fillValidCredentials();
    await component.onSubmit();
    expect(TestBed.inject(SessionContextService).clear).not.toHaveBeenCalled();
    expect(TestBed.inject(SessionContextService).establish).not.toHaveBeenCalled();
    expect(currentUser()).not.toBeNull();
  });

  /** b 在文档顺序上位于 a 之后。 */
  function follows(a: Element, b: Element): boolean {
    return (a.compareDocumentPosition(b) & Node.DOCUMENT_POSITION_FOLLOWING) !== 0;
  }

  // 找回入口紧跟口令框、在登录按钮之前；填的是邮箱时经导航状态带过去，地址栏里不留邮箱
  it('links to password recovery after the password and carries an entered email', async () => {
    await setUp();
    const host = fixture.nativeElement as HTMLElement;
    const link = host.querySelector<HTMLAnchorElement>('[data-testid="forgot-password"]')!;

    expect(link.getAttribute('href')).toBe('/auth/forgot-password');
    expect(follows(host.querySelector('#password')!, link)).toBe(true);
    expect(follows(link, host.querySelector('button[type="submit"]')!)).toBe(true);

    component.loginForm.usernameOrEmail().value.set('someone@example.test');
    fixture.detectChanges();
    link.click();
    expect(router.navigateByUrl).toHaveBeenLastCalledWith(
      expect.anything(),
      expect.objectContaining({ state: { email: 'someone@example.test' } }),
    );

    component.loginForm.usernameOrEmail().value.set('admin');
    fixture.detectChanges();
    link.click();
    expect(router.navigateByUrl).toHaveBeenLastCalledWith(
      expect.anything(),
      expect.objectContaining({ state: {} }),
    );
  });
  //#if (IncludeMultiTenancy || ExternalLogin)

  // 租户先于凭据（它决定凭据在哪里校验）；外部登录是替代路径，放在登录按钮之后
  it('puts the tenant before the credentials and external sign-in after the submit button', async () => {
    await setUp();
    await fixture.whenStable();
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    //#if (IncludeMultiTenancy)

    expect(
      follows(
        host.querySelector('[data-testid="tenant-row"]')!,
        host.querySelector('#usernameOrEmail')!,
      ),
    ).toBe(true);
    //#endif
    //#if (ExternalLogin)
    const divider = host.querySelector('[data-testid="external-divider"]')!;

    expect(follows(host.querySelector('button[type="submit"]')!, divider)).toBe(true);
    expect(follows(divider, host.querySelector('[data-testid="external-login-github"]')!)).toBe(
      true,
    );
    //#endif
  });
  //#endif

  /** 页面上显示出来的校验提示（未显示的 hlm-field-error 不渲染内容）。 */
  function shownErrors(): string[] {
    return Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('hlm-field-error'))
      .map((element) => element.textContent!.trim())
      .filter((text) => text.length > 0);
  }

  //#if (IncludeLocalization)
  /** 校验提示按错误类型取词条（`validation.<kind>`），参数来自校验器的错误对象，切换语言后随之更新。 */
  it('shows validation errors by kind, with their parameters, in the active language', async () => {
    vi.spyOn(navigator, 'languages', 'get').mockReturnValue(['en']);
    await setUp();
    const transloco = TestBed.inject(TranslocoService);
    transloco.setTranslation(
      { validation: { required: 'Required', minLength: 'At least {{minLength}}' } },
      'en',
    );
    transloco.setTranslation(
      { validation: { required: '必填', minLength: '至少 {{minLength}} 位' } },
      'zh-CN',
    );
    component.loginForm.usernameOrEmail().value.set('ab');
    await component.onSubmit();
    await fixture.whenStable();

    expect(shownErrors()).toEqual(['At least 3', 'Required']);

    transloco.setActiveLang('zh-CN');
    await fixture.whenStable();

    expect(shownErrors()).toEqual(['至少 3 位', '必填']);
  });

  // 标签与按钮文案经模板结构指令的 t 取得：切换语言后已渲染的表单要换成新语言。
  // 防不住"文案改回组件里的 translate()"——那一条由 setting-section.spec 钉住。
  it('re-renders form labels and the submit button when the language changes', async () => {
    await setUp();
    const transloco = TestBed.inject(TranslocoService);
    transloco.setTranslation({ login: { password: '密码', submit: '登录' } }, 'account/zh-CN');
    const host = fixture.nativeElement as HTMLElement;
    const passwordLabel = () => host.querySelector('label[for="password"]')!.textContent!.trim();
    const submitText = () => host.querySelector('button[type="submit"]')!.textContent!.trim();
    expect(passwordLabel()).toBe('account.login.password');
    expect(submitText()).toBe('account.login.submit');

    transloco.setActiveLang('zh-CN');
    await fixture.whenStable();

    expect(passwordLabel()).toBe('密码');
    expect(submitText()).toBe('登录');
  });

  //#else
  // 不含本地化时提示来自内置英文表（english-text.ts），参数同样取自校验器给出的错误对象
  it('shows validation errors by kind from the built-in English table', async () => {
    await setUp();
    component.loginForm.usernameOrEmail().value.set('ab');
    await component.onSubmit();
    await fixture.whenStable();

    expect(shownErrors()).toEqual(['Must be at least 3 characters.', 'This field is required.']);
  });

  //#endif
  it('does not send a login request when the form is invalid', async () => {
    await setUp();

    // 空表单直接提交：应停在原地并标记为已触碰，让校验信息显示出来。
    await component.onSubmit();

    expect(authService.login).not.toHaveBeenCalled();
    expect(component.loginForm().touched()).toBe(true);
    expect(component.isLoading()).toBe(false);
  });

  // 密码管理器按 autocomplete 取用与保存凭据：缺了它，已保存的账号口令填不进来
  it('marks the credential fields for password managers', async () => {
    await setUp();
    const host = fixture.nativeElement as HTMLElement;

    expect(host.querySelector('#usernameOrEmail')?.getAttribute('autocomplete')).toBe('username');
    expect(host.querySelector('#password')?.getAttribute('autocomplete')).toBe('current-password');
  });

  it('navigates to a safe local returnUrl after a successful login', async () => {
    queryParams = { returnUrl: '/platform/users' };
    await setUp();
    fillValidCredentials();

    await component.onSubmit();

    expect(authService.login).toHaveBeenCalled();
    expect(router.navigateByUrl).toHaveBeenCalledWith('/platform/users');
  });

  it('continues the external return address after completing the second step', async () => {
    navigationState = { twoFactorPrompt: PROMPT, returnUrl: '/workspace/settings/security' };
    await setUp();
    await (
      component as unknown as { onTwoFactorCompleted(): Promise<void> }
    ).onTwoFactorCompleted();
    expect(router.navigateByUrl).toHaveBeenCalledWith('/workspace/settings/security');
    expect(TestBed.inject(SessionContextService).establish).toHaveBeenCalled();
  });

  it('switches to the verification code step when two-factor is enabled, without loading the current user or navigating', async () => {
    await setUp();
    authService.login.mockReturnValue(
      of({ requiresTwoFactor: true, twoFactorToken: 'challenge-token' }) as never,
    );
    fillValidCredentials();

    await component.onSubmit();
    fixture.detectChanges();

    expect(authService.loadUser).not.toHaveBeenCalled();
    expect(router.navigate).not.toHaveBeenCalled();
    expect(
      (fixture.nativeElement as HTMLElement).querySelector('[data-testid="two-factor-challenge"]'),
    ).not.toBeNull();
  });

  it('sends a restricted session (organization requires two-factor) straight to the setup page without establishing session context', async () => {
    queryParams = { returnUrl: '/platform/users?offset=20' };
    await setUp();
    authService.loadUser.mockImplementation(() => {
      currentUser.set({ twoFactorSetupRequired: true });
      return of(undefined) as never;
    });
    fillValidCredentials();

    await component.onSubmit();

    // 原目标随之带去设置页，设置完成后回到那里
    expect(navigatedUrls()).toEqual([
      '/auth/two-factor-setup?returnUrl=%2Fplatform%2Fusers%3Foffset%3D20',
    ]);
    expect(router.navigate).not.toHaveBeenCalled();
    expect(TestBed.inject(SessionContextService).establish).not.toHaveBeenCalled();
  });

  // 第二步完成后才知道受限：外部登录带来的原目标同样随之带去设置页
  it('carries the return address from the second step to the setup page of a restricted session', async () => {
    navigationState = { twoFactorPrompt: PROMPT, returnUrl: '/workspace/settings/security' };
    await setUp();
    authService.loadUser.mockImplementation(() => {
      currentUser.set({ twoFactorSetupRequired: true });
      return of(undefined) as never;
    });

    await (
      component as unknown as { onTwoFactorCompleted(): Promise<void> }
    ).onTwoFactorCompleted();

    expect(navigatedUrls()).toEqual([
      '/auth/two-factor-setup?returnUrl=%2Fworkspace%2Fsettings%2Fsecurity',
    ]);
  });
  //#if (OpenIddictServer)

  // 授权端点把未登录的授权请求送来这里：登录后整页回到它，原样保留缓存请求的引用与证明
  it('returns to an authorization request with a full page load after signing in', async () => {
    const target =
      '/connect/authorize?client_id=resource&request_uri=urn%3Aabc&reauthentication=proof';
    queryParams = { returnUrl: target };
    await setUp();
    const loadPage = vi
      .spyOn(TestBed.inject(ReturnUrlService), 'loadPage')
      .mockImplementation(() => undefined);
    fillValidCredentials();

    await component.onSubmit();

    expect(loadPage).toHaveBeenCalledExactlyOnceWith(target);
    expect(router.navigateByUrl).not.toHaveBeenCalled();
  });
  //#endif

  /** 经 navigateByUrl 发出的导航，按地址栏形式读出（UrlTree 与字符串一视同仁）。 */
  function navigatedUrls(): string[] {
    return vi
      .mocked(router.navigateByUrl)
      .mock.calls.map(([url]) => (typeof url === 'string' ? url : router.serializeUrl(url)));
  }

  for (const hostile of [
    '//evil.example.com',
    'https://evil.example.com',
    'javascript://evil',
    '/\\evil.example.com',
  ]) {
    it(`discards an off-site returnUrl: ${hostile}`, async () => {
      queryParams = { returnUrl: hostile };
      await setUp();
      fillValidCredentials();

      await component.onSubmit();

      // 登录成功后被送到外站，是钓鱼链接最省事的一种玩法。
      expect(router.navigateByUrl).not.toHaveBeenCalled();
      expect(router.navigate).toHaveBeenCalled();
    });
  }

  it('shows only the failure toast, not the success toast, when establishing the session fails', async () => {
    await setUp();
    fillValidCredentials();
    (TestBed.inject(SessionContextService).establish as Mock).mockRejectedValue(
      new Error('settings unavailable'),
    );
    // toast 是模块级单例对象，组件与此处引用同一个，改它的方法组件立刻看得见。
    const success = vi.spyOn(toast, 'success').mockImplementation(() => '');
    const error = vi.spyOn(toast, 'error').mockImplementation(() => '');

    await component.onSubmit();

    // 先弹「登录成功」再弹「登录失败」的话，用户看到的是两条互相打脸的提示，
    // 而人还停在登录页——提示必须等会话真的建立完再发。
    expect(success).not.toHaveBeenCalled();
    expect(error).toHaveBeenCalled();
    expect(router.navigate).not.toHaveBeenCalled();
    expect(router.navigateByUrl).not.toHaveBeenCalled();
  });

  it('does not navigate and resets the loading state when login fails', async () => {
    await setUp();
    fillValidCredentials();
    authService.login.mockReturnValue(throwError(() => new Error('bad credentials')));

    await component.onSubmit();

    expect(router.navigate).not.toHaveBeenCalled();
    expect(router.navigateByUrl).not.toHaveBeenCalled();

    // 加载状态卡住的话，按钮会一直转，用户只能刷新页面。
    expect(component.isLoading()).toBe(false);
  });

  // 凭据被拒：服务端给出的原因显示在"登录"按钮上方，不弹 toast；改动任一输入即消失
  it('shows a rejected sign-in above the button and clears it when an input changes', async () => {
    await setUp();
    fillValidCredentials();
    const error = vi.spyOn(toast, 'error').mockImplementation(() => '');
    authService.login.mockReturnValue(
      throwError(() =>
        ApplicationHttpError.from(
          new HttpErrorResponse({
            status: 401,
            error: { code: 'Auth:InvalidCredentials', detail: 'Incorrect username or password.' },
          }),
        ),
      ),
    );

    await component.onSubmit();
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const alert = host.querySelector<HTMLElement>('[data-testid="login-error"]');
    expect(alert?.textContent?.trim()).toBe('Incorrect username or password.');
    expect(alert?.getAttribute('role')).toBe('alert');
    const submit = host.querySelector('button[type="submit"]')!;
    expect(alert!.compareDocumentPosition(submit) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(error).not.toHaveBeenCalled();

    const password = host.querySelector<HTMLInputElement>('#password')!;
    password.value = 'Admin@1234567';
    password.dispatchEvent(new Event('input', { bubbles: true }));
    fixture.detectChanges();

    expect(host.querySelector('[data-testid="login-error"]')).toBeNull();
  });
  //#if (IncludeMultiTenancy)

  // 租户区在表单之外：换了租户，上一个租户下的拒绝原因不再对应当前上下文
  it('drops the previous rejection once the tenant is switched', async () => {
    await setUp();
    await fixture.whenStable();
    fillValidCredentials();
    authService.login.mockReturnValue(
      throwError(() =>
        ApplicationHttpError.from(
          new HttpErrorResponse({
            status: 401,
            error: { code: 'Auth:InvalidCredentials', detail: 'Incorrect username or password.' },
          }),
        ),
      ),
    );
    await component.onSubmit();
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="login-error"]')).not.toBeNull();

    component.tenantName.set('acme');
    await component.applyTenantInput();
    fixture.detectChanges();

    expect(host.querySelector('[data-testid="login-error"]')).toBeNull();
  });
  //#endif

  // 不是 Problem Details 的 4xx（网关页、限流页）说不出填写哪里有问题：与故障一样用 toast，不当作凭据被拒
  it('keeps a 4xx without problem details in a toast', async () => {
    await setUp();
    fillValidCredentials();
    const error = vi.spyOn(toast, 'error').mockImplementation(() => '');
    authService.login.mockReturnValue(
      throwError(() =>
        ApplicationHttpError.from(
          new HttpErrorResponse({ status: 429, error: '<html>Too Many Requests</html>' }),
          { method: 'POST' },
        ),
      ),
    );

    await component.onSubmit();
    fixture.detectChanges();

    expect(error).toHaveBeenCalledOnce();
    expect(
      (fixture.nativeElement as HTMLElement).querySelector('[data-testid="login-error"]'),
    ).toBeNull();
  });

  // 连不上服务器这类全局问题仍用 toast，不占表单里的位置
  it('keeps network failures in a toast', async () => {
    await setUp();
    fillValidCredentials();
    const error = vi.spyOn(toast, 'error').mockImplementation(() => '');
    authService.login.mockReturnValue(
      throwError(() => ApplicationHttpError.from(new HttpErrorResponse({ status: 0 }))),
    );

    await component.onSubmit();
    fixture.detectChanges();

    expect(error).toHaveBeenCalledOnce();
    expect(
      (fixture.nativeElement as HTMLElement).querySelector('[data-testid="login-error"]'),
    ).toBeNull();
  });

  it('loads permissions before navigating when logging in to a protected returnUrl', async () => {
    queryParams = { returnUrl: '/protected' };
    await setUp();
    fillValidCredentials();

    // 用真实 Router 与 permissionGuard 跑完整条路：会话上下文须在跳转前建立，
    // 否则 guard 在空权限下把深链登录踢到 403。
    vi.mocked(router.navigateByUrl).mockRestore();
    (TestBed.inject(SessionContextService).establish as Mock).mockImplementation(async () => {
      authorization.setPermissions({
        permissions: [PERMISSIONS.users.default],
        isSuperAdmin: false,
        versionToken: 'r1',
      });
    });

    router.resetConfig([
      {
        path: 'protected',
        canActivate: [permissionGuard],
        data: { permission: PERMISSIONS.users.default },
        children: [],
      },
      { path: 'forbidden', children: [] },
    ]);

    await component.onSubmit();

    expect(router.url).toBe('/protected');
  });

  it('chooses the landing page by permissions rather than by role name or super-admin flag', async () => {
    await setUp();
    fillValidCredentials();

    // 用真实的 setPermissions 造状态而不打桩 canAccessPlatform，才能验证判据是权限而非角色；
    // 权限在 establish 时加载（凭据通过后旧主体先被清空）。
    const establish = TestBed.inject(SessionContextService).establish as Mock;
    establish.mockImplementation(async () => {
      authorization.setPermissions({ permissions: [], isSuperAdmin: true, versionToken: 'r1' });
    });

    await component.onSubmit();
    expect(router.navigate).toHaveBeenCalledWith(['/workspace']);

    (router.navigate as Mock).mockClear();
    establish.mockImplementation(async () => {
      authorization.setPermissions({
        permissions: [PERMISSIONS.users.default],
        isSuperAdmin: false,
        versionToken: 'r2',
      });
    });

    await component.onSubmit();
    expect(router.navigate).toHaveBeenCalledWith(['/platform']);
  });
  //#if (IncludeMultiTenancy)
  /** 主机名定案的三档结果；宿主定案与域名不表态必须分开，否则宿主域上残留上次记住的租户。 */
  describe('tenant resolution by host name', () => {
    // 匿名 by-host 只回租户名：回 id / 展示名 / 启用状态都会泄露"这个租户存在"
    const domainTenant = { name: 'acme' };

    /** 造出"上次记住的租户"：直接写存储，因为探测在组件构造时就跑。 */
    function rememberTenant(): void {
      localStorage.setItem('app.tenant', JSON.stringify({ key: 'remembered' }));
    }

    it('host name points to a tenant: pins that tenant and disallows manual selection', async () => {
      byHost = of({ decision: 'tenant' as const, tenant: domainTenant });
      await setUp();

      expect(TestBed.inject(TenantContextService).current()?.key).toBe('acme');
      expect(component.tenantLocked()).toBe(true);
      expect(component.tenantSelectionBlocked()).toBe(true);
    });

    it('host name resolves to the host: clears the remembered tenant and disallows selection', async () => {
      byHost = of({ decision: 'host' as const });
      rememberTenant();
      await setUp();

      expect(TestBed.inject(TenantContextService).current()).toBeNull();
      expect(component.tenantLocked()).toBe(true);
    });

    it('host name is undecided: keeps the remembered tenant and still allows manual selection', async () => {
      byHost = of({ decision: 'undecided' as const });
      rememberTenant();
      await setUp();

      expect(TestBed.inject(TenantContextService).current()?.key).toBe('remembered');
      expect(component.tenantLocked()).toBe(false);
      expect(component.tenantSelectionBlocked()).toBe(false);
    });

    // 域名指向的租户不在库里（或已停用）：这个部署当前用不了。
    // 不能退回"让用户自己挑一个"——挑了也会被域名覆盖。
    it('tenant from the host name is unavailable: clears it, stays locked, and shows a message', async () => {
      byHost = of({ decision: 'tenant' as const });
      await setUp();

      expect(TestBed.inject(TenantContextService).current()).toBeNull();
      expect(component.tenantLocked()).toBe(true);
      expect(component.tenantError()).toBeTruthy();
    });

    /** 探测失败不等于域名不表态：租户解析不了（404）或后端不可达时，不开放手选也不放行登录。 */
    it('probe failure is not treated as undecided: blocks manual selection and login and explains why', async () => {
      byHost = throwError(() => new Error('offline'));
      await setUp();
      fillValidCredentials();

      expect(component.hostProbe()).toBe('failed');
      expect(component.tenantSelectionBlocked()).toBe(true);
      expect(component.tenantError()).toBeTruthy();

      // 清除也算一次手动改租户：同样不放行，否则用户能在"不知道域名会怎么解析"时
      // 把上下文改成宿主，然后带着它去登录。
      TestBed.inject(TenantContextService).set(domainTenant.name);
      component.clearTenant();
      expect(TestBed.inject(TenantContextService).current()?.key).toBe('acme');

      await component.onSubmit();

      expect(authService.login).not.toHaveBeenCalled();
    });

    // 挡住之后必须给一条出路：瞬时故障不该让人只剩"刷新整页"，尤其表单可能已经填好。
    it('can retry after a probe failure and recovers once the retry gets a decision', async () => {
      byHost = throwError(() => new Error('offline'));
      await setUp();
      expect(component.authBlocked()).toBe(true);

      byHost = of({ decision: 'undecided' as const });
      await component.retryHostProbe();

      expect(component.hostProbe()).toBe('undecided');
      expect(component.authBlocked()).toBe(false);
      expect(component.tenantError()).toBeNull();
      expect(component.tenantSelectionBlocked()).toBe(false);
    });

    /** 探测未回来时不发认证请求：请求会落到域名对应的上下文，与界面显示的租户不一致。 */
    it('does not send an authentication request while the probe is pending', async () => {
      byHost = new Observable<TenantByHostOutputDto>(() => undefined);
      await setUp();
      fillValidCredentials();

      expect(component.authBlocked()).toBe(true);

      await component.onSubmit();

      expect(authService.login).not.toHaveBeenCalled();
    });

    // 反向的一条：域名已定案时禁止**选择**，但恰恰应该放行登录。
    // 把两件事共用一个条件，就会把子域名部署的登录整个挡死。
    it('blocks selection but still allows login when the host name is decided', async () => {
      byHost = of({ decision: 'tenant' as const, tenant: domainTenant });
      await setUp();
      fillValidCredentials();

      expect(component.tenantSelectionBlocked()).toBe(true);
      expect(component.authBlocked()).toBe(false);

      await component.onSubmit();

      expect(authService.login).toHaveBeenCalled();
    });
    //#if (ExternalLogin)

    it('passes the authorization return address to external login navigation', async () => {
      queryParams = { returnUrl: '/connect/authorize?client_id=resource&state=original' };
      await setUp();
      vi.spyOn(console, 'error').mockReturnValue(undefined);
      const externalLogin = vi
        .spyOn(TestBed.inject(AccountService), 'getExternalLoginUrl')
        .mockImplementation(() => {
          // 停在整页导航前，验证真实组件传给导航地址构造器的参数。
          throw new Error('Stop before browser navigation');
        });
      // 主机名探测在首帧之后才回来：重新渲染一次，按钮才从等待态解除禁用
      fixture.detectChanges();
      (fixture.nativeElement as HTMLElement)
        .querySelector<HTMLButtonElement>('[data-testid="external-login-google"]')!
        .click();
      expect(externalLogin).toHaveBeenCalledWith('google', queryParams['returnUrl']);
    });

    // 第三方登录走的是同一条约束：回调最终也落在按主机名解析出的那个上下文里。
    it('does not start external login while the probe is pending', async () => {
      byHost = new Observable<TenantByHostOutputDto>(() => undefined);
      await setUp();
      const externalLogin = vi
        .spyOn(TestBed.inject(AccountService), 'getExternalLoginUrl')
        .mockReturnValue('/api/v1/external-auth/github/challenge');

      const github = (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>(
        '[data-testid="external-login-github"]',
      )!;
      expect(github.disabled).toBe(true);
      github.click();

      expect(externalLogin).not.toHaveBeenCalled();
    });
    //#endif

    // 探测未回来时不接受手选：它一回来就会覆盖上下文，此时选的会被无声换掉。
    it('disables the tenant area and ignores manual selection while the probe is pending', async () => {
      byHost = new Observable<TenantByHostOutputDto>(() => undefined);
      await setUp();

      expect(component.tenantSelectionBlocked()).toBe(true);

      component.tenantName.set('acme');
      await component.onConfirmTenant();

      expect(TestBed.inject(TenantContextService).current()).toBeNull();
    });

    // 「切换租户」展开的输入框：填名称切到该租户，留空回到宿主，应用后收起
    it('switches to the typed tenant and back to the host when left empty', async () => {
      byHost = of({ decision: 'undecided' as const });
      await setUp();
      const tenantContext = TestBed.inject(TenantContextService);

      component.tenantEditing.set(true);
      component.tenantName.set('acme');
      await component.applyTenantInput();
      expect(tenantContext.current()?.key).toBe('acme');
      expect(component.tenantEditing()).toBe(false);

      component.tenantEditing.set(true);
      component.tenantName.set('  ');
      await component.applyTenantInput();
      expect(tenantContext.current()).toBeNull();
    });
  });
  //#endif
  //#if (ExternalLogin)

  // 入口按部署实际登记的提供商渲染：固定两个按钮会让未配置的那个点进 503。
  describe('external login entries', () => {
    function entries(): string[] {
      return [
        ...(fixture.nativeElement as HTMLElement).querySelectorAll<HTMLElement>(
          '[data-testid^="external-login-"]',
        ),
      ].map((element) => element.dataset['testid']!);
    }

    it('shows no entry while the provider list is still loading', async () => {
      externalProviders = new Observable<ExternalLoginProvidersOutputDto>(() => undefined);
      await setUp();

      expect(entries()).toEqual([]);
    });

    it.each([
      [[], []],
      // 页面只内置两种入口；派生项目登记的其他提供商要自行补入口，此时不留空分隔区。
      [['custom'], []],
      [['github'], ['external-login-github']],
      [['google'], ['external-login-google']],
      [
        ['github', 'google'],
        ['external-login-github', 'external-login-google'],
      ],
    ])('renders exactly the configured providers %j', async (providers, expected) => {
      externalProviders = of({ providers });
      await setUp();

      expect(entries()).toEqual(expected);
    });

    it('keeps the divider out when no built-in entry can be rendered', async () => {
      externalProviders = of({ providers: ['custom'] });
      await setUp();

      expect(
        (fixture.nativeElement as HTMLElement).querySelector('[data-testid="external-divider"]'),
      ).toBeNull();
    });

    it('shows the normalized failure with its trace ID and clears it on retry', async () => {
      externalProviders = throwError(() =>
        ApplicationHttpError.from(
          new HttpErrorResponse({
            status: 503,
            error: { status: 503, title: 'Service Unavailable', traceId: '00-trace-01' },
          }),
        ),
      );
      await setUp();

      const trace = () =>
        (fixture.nativeElement as HTMLElement).querySelector(
          '[data-testid="external-login-failure-detail"]',
        );
      expect(trace()?.textContent?.trim()).toBe(
        'An unexpected error occurred. Please refresh the page and try again. (Trace ID: 00-trace-01)',
      );

      externalProviders = of({ providers: ['google'] });
      (fixture.nativeElement as HTMLElement)
        .querySelector<HTMLButtonElement>('[data-testid="external-login-retry"]')!
        .click();
      fixture.detectChanges();

      expect(trace()).toBeNull();
      expect(entries()).toEqual(['external-login-google']);
    });

    it('reports a failed provider list instead of treating it as unconfigured, and retries', async () => {
      externalProviders = throwError(() => new Error('unavailable'));
      await setUp();

      expect(entries()).toEqual(['external-login-retry']);

      externalProviders = of({ providers: ['github'] });
      (fixture.nativeElement as HTMLElement)
        .querySelector<HTMLButtonElement>('[data-testid="external-login-retry"]')!
        .click();
      fixture.detectChanges();

      expect(entries()).toEqual(['external-login-github']);
    });
  });
  //#endif
});
