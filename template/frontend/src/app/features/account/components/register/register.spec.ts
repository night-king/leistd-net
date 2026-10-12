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

    await TestBed.configureTestingModule({
      imports: [Register],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        //#if (IncludeLocalization)
        ...provideTranslocoTesting(['en']),
        //#endif
        { provide: AccountService, useValue: accountService },
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
    component.registerForm.confirmPassword().value.set('RegisterSpec!Pw1');
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
    expect(autocomplete('confirmPassword')).toBe('new-password');
  });

  it('treats the form as invalid when the two passwords differ', async () => {
    await setUp();
    fillValidForm();
    // 长度合法但与上面不同：本用例测的是"两次不一致"，
    // 不该因为长度不足而由另一条规则拒绝——那样断言会通过，但通过的原因是错的
    component.registerForm.confirmPassword().value.set('RegisterSpec!Pw2');

    await component.onSubmit();

    expect(accountService.register).not.toHaveBeenCalled();
  });

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
