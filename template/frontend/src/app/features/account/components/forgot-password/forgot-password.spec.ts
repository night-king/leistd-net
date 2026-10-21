//#if (Email)
import { signal } from '@angular/core';
//#endif
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
//#if (Email)
// prettier-ignore
import {
  //#if (IncludeMultiTenancy)
  Observable,
  //#endif
  of,
  throwError,
} from 'rxjs';
//#endif

import { ForgotPassword } from './forgot-password';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../../../core/i18n/transloco.testing';
//#endif
//#if (Email)
import { SecurityConfigService } from '../../../../core/services/security-config-service';
//#endif
//#if (Email && IncludeMultiTenancy)
import { TenantContextService } from '../../../../core/services/tenant-context-service';
import { TenantByHostOutputDto } from '../../dtos/tenant-by-host.dto';
//#endif
//#if (Email)
import { AccountService } from '../../services/account-service';

import type { MockedObject } from 'vitest';
//#endif

/**
 * 忘记密码：两步在同一页，先发码再填码与新口令；发码后的说明不承认邮箱是否对应账号。
 * 不含邮件或部署缺验证码前提时只给"请管理员重置"的说明。
 */
describe('ForgotPassword', () => {
  let fixture: ComponentFixture<ForgotPassword>;
  let host: HTMLElement;
  let router: Router;
  let queryParams: Record<string, string>;
  //#if (Email)
  let component: ForgotPassword;
  //#if (IncludeMultiTenancy)
  let account: Pick<
    MockedObject<AccountService>,
    'getCaptcha' | 'sendPasswordResetCode' | 'resetPassword' | 'getTenantByHost'
  >;
  /** 主机名探测的应答；默认域名不表态，与本机开发一致。 */
  let byHost: Observable<TenantByHostOutputDto>;
  /** 建组件前记在本地的租户（上一次登录页选过的）。 */
  let rememberedTenant: string | null;
  //#else
  let account: Pick<
    MockedObject<AccountService>,
    'getCaptcha' | 'sendPasswordResetCode' | 'resetPassword'
  >;
  //#endif
  let available: boolean;
  let navigationState: { email?: string } | undefined;
  //#endif

  async function setUp(): Promise<void> {
    //#if (Email)
    account = {
      getCaptcha: vi.fn().mockName('AccountService.getCaptcha'),
      sendPasswordResetCode: vi.fn().mockName('AccountService.sendPasswordResetCode'),
      resetPassword: vi.fn().mockName('AccountService.resetPassword'),
      //#if (IncludeMultiTenancy)
      getTenantByHost: vi.fn().mockName('AccountService.getTenantByHost'),
      //#endif
    };
    //#if (IncludeMultiTenancy)
    account.getTenantByHost.mockImplementation(() => byHost);
    //#endif
    account.getCaptcha.mockReturnValue(
      of({ captchaToken: 'captcha-token', captchaImageBase64: 'data:image/png;base64,' }),
    );
    account.sendPasswordResetCode.mockReturnValue(
      of({
        challengeId: '11111111-1111-1111-1111-111111111111',
        expiresInSeconds: 300,
        retryAfterSeconds: 37,
      }),
    );
    account.resetPassword.mockReturnValue(of(undefined));

    //#endif
    await TestBed.configureTestingModule({
      imports: [ForgotPassword],
      // prettier-ignore
      providers: [
        provideRouter([]),
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { queryParamMap: convertToParamMap(queryParams) } },
        },
        //#if (IncludeLocalization)
        ...provideTranslocoTesting(['en']),
        //#endif
        //#if (Email)
        { provide: AccountService, useValue: account },
        {
          provide: SecurityConfigService,
          useValue: {
            config: signal({
              registrationEnabled: true,
              enableEmailVerification: false,
              emailVerificationAvailable: available,
            }),
            refresh: vi.fn(),
          },
        },
        //#endif
      ],
    }).compileComponents();

    router = TestBed.inject(Router);
    //#if (Email && IncludeMultiTenancy)
    if (rememberedTenant) {
      TestBed.inject(TenantContextService).set(rememberedTenant);
    }
    //#endif
    //#if (Email)
    if (navigationState) {
      vi.spyOn(router, 'currentNavigation').mockReturnValue({
        extras: { state: navigationState },
      } as never);
    }
    //#endif
    vi.spyOn(router, 'navigate').mockResolvedValue(true);
    fixture = TestBed.createComponent(ForgotPassword);
    //#if (Email)
    component = fixture.componentInstance;
    //#endif
    host = fixture.nativeElement as HTMLElement;
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }
  //#if (Email)

  beforeEach(() => {
    queryParams = {};
    available = true;
    navigationState = undefined;
    //#if (IncludeMultiTenancy)
    byHost = of({ decision: 'undecided' as const });
    rememberedTenant = null;
    localStorage.clear();
    //#endif
  });
  //#if (IncludeMultiTenancy)
  afterEach(() => localStorage.clear());
  //#endif

  async function sendCode(): Promise<void> {
    component.sendForm.email().value.set('someone@example.test');
    component.sendForm.captchaCode().value.set('AB12');
    await component.sendCode();
    fixture.detectChanges();
  }

  it('takes the email entered on the sign-in page', async () => {
    navigationState = { email: 'someone@example.test' };
    await setUp();

    expect(component.sendForm.email().value()).toBe('someone@example.test');
  });

  it('does not send a code until the email and captcha are filled', async () => {
    await setUp();

    await component.sendCode();

    expect(account.sendPasswordResetCode).not.toHaveBeenCalled();
    expect(component.sendForm().touched()).toBe(true);
  });

  // 说明不承认邮箱是否对应账号；重发要等服务端给的间隔
  it('moves to the second step with a neutral note and the server countdown', async () => {
    await setUp();

    await sendCode();

    expect(account.sendPasswordResetCode).toHaveBeenCalledWith({
      email: 'someone@example.test',
      captchaToken: 'captcha-token',
      captchaCode: 'AB12',
    });
    //#if (IncludeLocalization)
    expect(host.querySelector('[data-testid="forgot-sent"]')!.textContent).toContain(
      'account.forgotPassword.sent',
    );
    //#else
    expect(host.querySelector('[data-testid="forgot-sent"]')!.textContent).toContain(
      'If someone@example.test is a verified email',
    );
    //#endif
    expect(host.querySelector('#code')).not.toBeNull();
    expect(component.countdown.remaining()).toBe(37);
    expect(host.querySelector('[data-testid="resend-reset-code"]')).toBeNull();
  });

  // 图形验证码令牌只能用一次：发码失败后换一张
  it('stays on the first step and loads a new captcha when sending fails', async () => {
    await setUp();
    account.sendPasswordResetCode.mockReturnValue(throwError(() => new Error('captcha')));
    account.getCaptcha.mockClear();

    await sendCode();

    expect(host.querySelector('#code')).toBeNull();
    expect(account.getCaptcha).toHaveBeenCalledOnce();
    expect(component.isLoading()).toBe(false);
  });

  it('goes back to the first step with the email kept to send again', async () => {
    await setUp();
    await sendCode();
    component.countdown.stop();
    fixture.detectChanges();

    host.querySelector<HTMLButtonElement>('[data-testid="resend-reset-code"]')!.click();
    fixture.detectChanges();

    expect(host.querySelector('#code')).toBeNull();
    expect(component.sendForm.email().value()).toBe('someone@example.test');
  });

  // 不自动登录：开着两步验证的账号仍要走第二步，统一回登录页
  it('resets with the challenge and returns to the sign-in page', async () => {
    await setUp();
    await sendCode();
    component.resetForm.code().value.set(' 123456 ');
    component.resetForm.newPassword().value.set('ForgotSpec!Pw1');

    await component.reset();

    expect(account.resetPassword).toHaveBeenCalledWith({
      email: 'someone@example.test',
      challengeId: '11111111-1111-1111-1111-111111111111',
      code: '123456',
      newPassword: 'ForgotSpec!Pw1',
    });
    expect(router.navigate).toHaveBeenCalledWith(['/auth/login'], { queryParams: undefined });
  });

  // 由授权端点或业务深链进入时，找回后回到登录页仍要接续原目标；是否可接续由登录页判定
  it('hands the return address back to the sign-in page', async () => {
    queryParams = { returnUrl: '/platform/users?tab=roles' };
    await setUp();
    await sendCode();
    component.resetForm.code().value.set('123456');
    component.resetForm.newPassword().value.set('ForgotSpec!Pw1');

    await component.reset();

    expect(router.navigate).toHaveBeenCalledWith(['/auth/login'], {
      queryParams: { returnUrl: '/platform/users?tab=roles' },
    });
    expect(host.querySelector('[data-testid="back-to-login"]')!.getAttribute('href')).toBe(
      '/auth/login?returnUrl=%2Fplatform%2Fusers%3Ftab%3Droles',
    );
  });

  it('stays on the second step when the reset is rejected', async () => {
    await setUp();
    await sendCode();
    account.resetPassword.mockReturnValue(throwError(() => new Error('invalid code')));
    component.resetForm.code().value.set('000000');
    component.resetForm.newPassword().value.set('ForgotSpec!Pw1');

    await component.reset();

    expect(router.navigate).not.toHaveBeenCalled();
    expect(component.isLoading()).toBe(false);
  });

  it('does not submit a new password shorter than the policy', async () => {
    await setUp();
    await sendCode();
    component.resetForm.code().value.set('123456');
    component.resetForm.newPassword().value.set('short');

    await component.reset();

    expect(account.resetPassword).not.toHaveBeenCalled();
  });
  //#if (IncludeMultiTenancy)

  // 挑战按租户隔离：直接打开本页时，记住的租户可能与服务端按域名采用的不同，定案前不发码
  it('waits for the site address to settle the tenant before sending', async () => {
    byHost = new Observable<TenantByHostOutputDto>(() => undefined);
    await setUp();

    expect(host.querySelector('[data-testid="tenant-row"]')).toBeNull();
    expect(host.querySelector<HTMLButtonElement>('[data-testid="send-reset-code"]')!.disabled).toBe(
      true,
    );
    await sendCode();
    expect(account.sendPasswordResetCode).not.toHaveBeenCalled();
  });

  it('replaces a remembered tenant with the host when the site address belongs to the host', async () => {
    rememberedTenant = 'acme';
    byHost = of({ decision: 'host' as const });
    await setUp();

    expect(TestBed.inject(TenantContextService).current()).toBeNull();
    expect(host.querySelector('[data-testid="tenant-row"]')!.textContent).not.toContain('acme');
  });

  it('shows the tenant the site address points to', async () => {
    rememberedTenant = 'old';
    byHost = of({ decision: 'tenant' as const, tenant: { name: 'acme' } });
    await setUp();

    expect(host.querySelector('[data-testid="tenant-row"]')!.textContent).toContain('acme');
  });

  it('blocks sending until a failed tenant lookup is retried successfully', async () => {
    byHost = throwError(() => new Error('offline'));
    await setUp();

    await sendCode();
    expect(account.sendPasswordResetCode).not.toHaveBeenCalled();

    byHost = of({ decision: 'undecided' as const });
    host.querySelector<HTMLButtonElement>('[data-testid="tenant-probe-retry"]')!.click();
    await fixture.whenStable();
    fixture.detectChanges();
    await sendCode();

    expect(account.sendPasswordResetCode).toHaveBeenCalledOnce();
  });
  //#endif

  // 部署缺验证码前提：给表单只会得到暂不可用
  it('only explains when the deployment cannot send codes', async () => {
    available = false;
    await setUp();

    expect(host.querySelector('[data-testid="forgot-unavailable"]')).not.toBeNull();
    expect(host.querySelector('form')).toBeNull();
    expect(host.querySelector('[data-testid="back-to-login"]')).not.toBeNull();
  });
  //#else

  beforeEach(() => {
    queryParams = {};
  });

  it('asks for an administrator reset and links back to sign-in', async () => {
    await setUp();

    expect(host.querySelector('[data-testid="forgot-unavailable"]')).not.toBeNull();
    expect(host.querySelector('form')).toBeNull();
    expect(host.querySelector('[data-testid="back-to-login"]')!.getAttribute('href')).toBe(
      '/auth/login',
    );
    expect(router.navigate).not.toHaveBeenCalled();
  });
  //#endif
});
