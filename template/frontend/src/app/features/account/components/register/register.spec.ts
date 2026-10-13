import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';

import { Register } from './register';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../../../core/i18n/transloco.testing';
//#endif
import { AccountService } from '../../services/account-service';

import type { MockedObject } from 'vitest';

/** 注册提交路径：唯一匿名可达的写接口，覆盖表单校验、验证码令牌与失败后刷新验证码。 */
describe('Register', () => {
  let fixture: ComponentFixture<Register>;
  let component: Register;
  let router: Router;
  //#if (Email)
  let accountService: Pick<
    MockedObject<AccountService>,
    'register' | 'getCaptcha' | 'getSecurityConfig' | 'sendEmailCode'
  >;
  //#else
  let accountService: Pick<MockedObject<AccountService>, 'register' | 'getCaptcha'>;
  //#endif
  //#if (ExternalLogin)
  let externalAuth: Pick<
    MockedObject<AccountService>,
    'getExternalLoginProviders' | 'getExternalLoginUrl'
  >;
  //#endif
  let queryParams: Record<string, string>;

  //#if (Email)
  async function setUp(enableEmailVerification = false): Promise<void> {
  //#else
  async function setUp(): Promise<void> {
  //#endif
    accountService = {
      register: vi.fn().mockName('AccountService.register'),
      getCaptcha: vi.fn().mockName('AccountService.getCaptcha'),
      //#if (Email)
      getSecurityConfig: vi.fn().mockName('AccountService.getSecurityConfig'),
      sendEmailCode: vi.fn().mockName('AccountService.sendEmailCode'),
      //#endif
    };
    accountService.register.mockReturnValue(of(undefined));
    accountService.getCaptcha.mockReturnValue(
      of({ captchaToken: 'token-1', captchaImage: 'data:image/png;base64,' }) as never,
    );
    //#if (Email)
    accountService.getSecurityConfig.mockReturnValue(of({ enableEmailVerification }) as never);
    accountService.sendEmailCode.mockReturnValue(
      of({
        challengeId: '11111111-1111-1111-1111-111111111111',
        expiresInSeconds: 300,
        retryAfterSeconds: 37,
      }) as never,
    );
    //#endif
    //#if (ExternalLogin)
    externalAuth = {
      getExternalLoginProviders: vi.fn().mockReturnValue(of({ providers: ['github'] })),
      getExternalLoginUrl: vi.fn(),
    };
    //#endif

    await TestBed.configureTestingModule({
      imports: [Register],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        //#if (IncludeLocalization)
        ...provideTranslocoTesting(['en']),
        //#endif
        //#if (ExternalLogin)
        { provide: AccountService, useValue: { ...accountService, ...externalAuth } },
        //#else
        { provide: AccountService, useValue: accountService },
        //#endif
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { queryParamMap: convertToParamMap(queryParams) } },
        },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(Register);
    component = fixture.componentInstance;
    router = TestBed.inject(Router);

    vi.spyOn(router, 'navigate').mockResolvedValue(true);
    fixture.detectChanges();
  }

  function fillValidForm(): void {
    component.registerForm.email().value.set('someone@example.test');
    component.registerForm.username().value.set('someone');
    component.registerForm.captchaCode().value.set('1234');
    component.registerForm.password().value.set('RegisterSpec!Pw1');
  }

  beforeEach(() => {
    queryParams = {};
  });

  it('does not submit the registration request when the form is invalid', async () => {
    await setUp();

    await component.onSubmit();

    expect(accountService.register).not.toHaveBeenCalled();
    expect(component.registerForm().touched()).toBe(true);
    expect(component.isLoading()).toBe(false);
  });

  // 新口令标成 new-password：浏览器据此生成强口令并在注册后保存，而不是填入别处的旧口令
  it('marks the account fields for password managers', async () => {
    await setUp();
    const autocomplete = (id: string) =>
      (fixture.nativeElement as HTMLElement).querySelector(`#${id}`)?.getAttribute('autocomplete');

    expect(autocomplete('email')).toBe('email');
    expect(autocomplete('username')).toBe('username');
    expect(autocomplete('password')).toBe('new-password');
  });

  // 图形验证码紧挨提交；不再要求第二遍输入密码，由密码框的显隐切换代替
  it('orders the fields so the image captcha comes right before submitting', async () => {
    await setUp();
    const ids = [...(fixture.nativeElement as HTMLElement).querySelectorAll('form input[id]')].map(
      (input) => input.id,
    );

    expect(ids).toEqual(['email', 'username', 'password', 'captchaCode']);
  });
  //#if (Email)

  // 获取邮件验证码前要先填图形验证码，所以邮件验证码排在它后面
  it('puts the email code after the image captcha when email verification is on', async () => {
    await setUp(true);
    // 安全配置随首帧异步取回：再渲染一次，邮件验证码字段才出现
    fixture.detectChanges();
    await fixture.whenStable();
    const ids = [...(fixture.nativeElement as HTMLElement).querySelectorAll('form input[id]')].map(
      (input) => input.id,
    );

    expect(ids).toEqual(['email', 'username', 'password', 'captchaCode', 'emailVerificationCode']);
  });
  //#endif
  //#if (ExternalLogin)

  // 首次外部登录即建账号：注册页给同一组入口，回跳地址与本页一致
  it('offers the configured external providers and passes the return address', async () => {
    queryParams = { returnUrl: '/connect/authorize?client_id=resource' };
    await setUp();
    vi.spyOn(console, 'error').mockReturnValue(undefined);
    externalAuth.getExternalLoginUrl.mockImplementation(() => {
      // 停在整页导航前，验证传给导航地址构造器的参数
      throw new Error('Stop before browser navigation');
    });

    (fixture.nativeElement as HTMLElement)
      .querySelector<HTMLButtonElement>('[data-testid="external-login-github"]')!
      .click();

    expect(externalAuth.getExternalLoginUrl).toHaveBeenCalledWith(
      'github',
      '/connect/authorize?client_id=resource',
    );
  });
  //#endif

  // 进度只反映口令策略的最小长度：不另立复杂度规则，够长即达标
  it('fills the length progress toward the minimum and reports how many characters remain', async () => {
    await setUp();
    const password = component.registerForm.password().value;

    password.set('abcdef');
    expect(component.passwordStrength()).toEqual({ met: false, filled: 2, remaining: 6 });

    password.set('abcdefghijkl');
    expect(component.passwordStrength()).toEqual({ met: true, filled: 4, remaining: 0 });
  });

  it('does not submit without a captcha token and resets loading', async () => {
    await setUp();
    fillValidForm();
    component.captchaData.set(null);

    await component.onSubmit();

    // 没有令牌就提交，服务端必然拒绝；本地先拦下来才不会白跑一趟并把按钮卡在加载态。
    expect(accountService.register).not.toHaveBeenCalled();
    expect(component.isLoading()).toBe(false);
  });

  it('navigates to the login page with returnUrl after a successful registration', async () => {
    queryParams = { returnUrl: '/platform/users' };
    await setUp();
    fillValidForm();

    await component.onSubmit();

    expect(accountService.register).toHaveBeenCalled();
    expect(router.navigate).toHaveBeenCalledWith(['/auth/login'], {
      queryParams: { returnUrl: '/platform/users' },
    });
  });

  it('omits empty query params when there is no returnUrl', async () => {
    await setUp();
    fillValidForm();

    await component.onSubmit();

    expect(router.navigate).toHaveBeenCalledWith(['/auth/login'], { queryParams: undefined });
  });

  it('refreshes the captcha and resets loading without navigating on failure', async () => {
    await setUp();
    fillValidForm();
    accountService.register.mockReturnValue(throwError(() => new Error('captcha mismatch')));
    accountService.getCaptcha.mockClear();

    await component.onSubmit();

    // 验证码是一次性的：失败后不换一张，用户只能一直提交同一个必然失败的组合。
    expect(accountService.getCaptcha).toHaveBeenCalled();
    expect(router.navigate).not.toHaveBeenCalled();
    expect(component.isLoading()).toBe(false);
  });
  //#if (Email)

  it('keeps the challenge and uses the server countdown after sending the email code', async () => {
    await setUp(true);
    fillValidForm();

    await component.sendEmailCode();

    expect(component.countdown()).toBe(37);
  });

  it('submits the nested challenge contract when email verification is on', async () => {
    await setUp(true);
    fillValidForm();
    await component.sendEmailCode();
    component.registerForm.emailVerificationCode().value.set('123456');

    await component.onSubmit();

    expect(accountService.register).toHaveBeenCalledWith(
      expect.objectContaining({
        emailVerification: {
          challengeId: '11111111-1111-1111-1111-111111111111',
          code: '123456',
        },
      }),
    );
  });

  it('invalidates the challenge and blocks submit when the email changes afterwards', async () => {
    await setUp(true);
    fillValidForm();
    await component.sendEmailCode();
    component.registerForm.emailVerificationCode().value.set('123456');

    component.registerForm.email().value.set('changed@example.test');
    fixture.detectChanges();
    await fixture.whenStable();
    await component.onSubmit();

    expect(accountService.register).not.toHaveBeenCalled();
  });
  //#endif
});
