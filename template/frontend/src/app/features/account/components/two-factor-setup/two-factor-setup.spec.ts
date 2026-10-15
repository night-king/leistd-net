import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';

import { TwoFactorSetup } from './two-factor-setup';
import { ApplicationHttpError } from '../../../../core/errors/application-http-error';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../../../core/i18n/transloco.testing';
//#endif
import { AccountService } from '../../services/account-service';

import type { MockedObject } from 'vitest';

/**
 * 开始设置会在服务端生成待启用的密钥（POST），属于写请求：
 * 只在 `ngOnInit` 发起，构造组件本身不改变服务端状态。
 */
describe('TwoFactorSetup', () => {
  let fixture: ComponentFixture<TwoFactorSetup>;
  let account: Pick<MockedObject<AccountService>, 'beginTwoFactorSetup' | 'enableTwoFactor'>;

  const host = () => fixture.nativeElement as HTMLElement;

  beforeEach(() => {
    account = {
      beginTwoFactorSetup: vi.fn().mockName('AccountService.beginTwoFactorSetup'),
      enableTwoFactor: vi.fn().mockName('AccountService.enableTwoFactor'),
    };
    account.beginTwoFactorSetup.mockReturnValue(
      of({
        setupId: 'setup-1',
        secret: 'JBSWY3DPEHPK3PXP',
        otpAuthUri: 'otpauth://totp/App:alice?secret=JBSWY3DPEHPK3PXP',
      }),
    );
    TestBed.configureTestingModule({
      imports: [TwoFactorSetup],
      // 关闭本地化时只剩一项，prettier 会要求并成一行；条件块不能随形态换折行方式
      // prettier-ignore
      providers: [
        { provide: AccountService, useValue: account },
        //#if (IncludeLocalization)
        ...provideTranslocoTesting(['en']),
        //#endif
      ],
    });
    fixture = TestBed.createComponent(TwoFactorSetup);
  });

  it('starts the setup on init, not on construction, and only once', async () => {
    expect(account.beginTwoFactorSetup).not.toHaveBeenCalled();

    fixture.detectChanges();
    await fixture.whenStable();

    expect(account.beginTwoFactorSetup).toHaveBeenCalledOnce();
    expect(host().querySelector('[data-testid="two-factor-secret"]')?.textContent?.trim()).toBe(
      'JBSW Y3DP EHPK 3PXP',
    );
  });

  it('labels the code input through the field label', async () => {
    fixture.detectChanges();
    await fixture.whenStable();

    const label = host().querySelector('label[hlmFieldLabel]');
    expect(label?.getAttribute('for')).toBe('two-factor-setup-code');
    expect(host().querySelector('input#two-factor-setup-code')).not.toBeNull();
  });

  it('offers a retry after a failed start and starts again only when asked', async () => {
    account.beginTwoFactorSetup.mockReturnValueOnce(
      throwError(() => new HttpErrorResponse({ status: 500, statusText: 'Server Error' })),
    );
    fixture.detectChanges();
    await fixture.whenStable();

    expect(account.beginTwoFactorSetup).toHaveBeenCalledOnce();
    expect(host().querySelector('[data-testid="two-factor-setup"]')).toBeNull();
    const retry = host().querySelector<HTMLButtonElement>('button[variant="link"]');
    expect(retry, 'retry button should be rendered').not.toBeNull();

    retry!.click();
    await fixture.whenStable();

    expect(account.beginTwoFactorSetup).toHaveBeenCalledTimes(2);
    expect(host().querySelector('[data-testid="two-factor-setup"]')).not.toBeNull();
  });

  it('confirms the setup it started, by its id', async () => {
    account.enableTwoFactor.mockReturnValue(of({ recoveryCodes: ['code-1'] }));
    const enabled = vi.fn();
    fixture.componentInstance.enabled.subscribe(enabled);
    fixture.detectChanges();
    await fixture.whenStable();

    await typeCode('123456');
    submitButton().click();
    await fixture.whenStable();

    expect(account.enableTwoFactor).toHaveBeenCalledWith('setup-1', '123456');
    expect(enabled).toHaveBeenCalledWith(['code-1']);
  });

  // 失效的设置再也确认不了：二维码、密钥与已输入的码都不留在页面上，只给"重新开始"
  it('clears the key and the code when the setup expired, and starts a fresh one on request', async () => {
    account.enableTwoFactor.mockReturnValue(
      throwError(() => failure('Auth:TwoFactorSetupExpired')),
    );
    fixture.detectChanges();
    await fixture.whenStable();
    await typeCode('123456');

    submitButton().click();
    await fixture.whenStable();

    expect(host().querySelector('[data-testid="two-factor-secret"]')).toBeNull();
    expect(host().querySelector('[data-testid="two-factor-qr"]')).toBeNull();
    expect(host().querySelector('input#two-factor-setup-code')).toBeNull();
    // 不只是不显示：作废的密钥与已输入的码也不留在组件状态里
    expect(fixture.componentInstance['secret']()).toBeNull();
    expect(fixture.componentInstance['qrDataUrl']()).toBeNull();
    expect(fixture.componentInstance['code']()).toBe('');
    const restart = host().querySelector<HTMLButtonElement>(
      '[data-testid="two-factor-setup-restart"]',
    );
    expect(restart, 'restart button should be rendered').not.toBeNull();

    restart!.click();
    await fixture.whenStable();

    expect(account.beginTwoFactorSetup).toHaveBeenCalledTimes(2);
    expect(host().querySelector('[data-testid="two-factor-secret"]')).not.toBeNull();
    expect((host().querySelector('input#two-factor-setup-code') as HTMLInputElement).value).toBe(
      '',
    );
  });

  // 二维码异步生成：设置失效之后才算完的那一份，不能再写回组件，也不能盖住重新开始后的新二维码
  it('drops a QR code that finishes after its setup expired', async () => {
    let finishOld!: (value: string) => void;
    // 第一次生成挂起，模拟二维码晚到；之后照常
    const renderQrCode = vi
      .spyOn(TwoFactorSetup.prototype as unknown as QrRenderer, 'renderQrCode')
      .mockImplementationOnce(() => new Promise<string>((resolve) => (finishOld = resolve)));
    account.enableTwoFactor.mockReturnValue(
      throwError(() => failure('Auth:TwoFactorSetupExpired')),
    );
    fixture.detectChanges();
    await fixture.whenStable();
    await typeCode('123456');
    submitButton().click();
    await fixture.whenStable();

    finishOld('data:image/png;base64,stale');
    await Promise.resolve();
    fixture.detectChanges();

    expect(fixture.componentInstance['qrDataUrl']()).toBeNull();
    renderQrCode.mockResolvedValueOnce('data:image/png;base64,fresh');
    host().querySelector<HTMLButtonElement>('[data-testid="two-factor-setup-restart"]')!.click();
    await fixture.whenStable();
    await Promise.resolve();
    expect(fixture.componentInstance['qrDataUrl']()).toBe('data:image/png;base64,fresh');
  });

  // 会话不够新：先出示当前口令，带上口令重新开始；不显示任何密钥
  it('asks for the current password when the session is not recent and retries with it', async () => {
    account.beginTwoFactorSetup.mockReturnValueOnce(
      throwError(() => failure('Auth:ReauthenticationRequired')),
    );
    fixture.detectChanges();
    await fixture.whenStable();

    const form = host().querySelector('[data-testid="two-factor-setup-password"]');
    expect(form, 'password step should be rendered').not.toBeNull();
    expect(host().querySelector('[data-testid="two-factor-secret"]')).toBeNull();

    const input = form!.querySelector<HTMLInputElement>('input[type="password"]')!;
    input.value = 'current-password';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    form!.querySelector<HTMLButtonElement>('button[type="submit"]')!.click();
    await fixture.whenStable();

    expect(account.beginTwoFactorSetup).toHaveBeenLastCalledWith({ password: 'current-password' });
    expect(host().querySelector('[data-testid="two-factor-secret"]')).not.toBeNull();
  });

  // 没有本地口令可出示：只能重新登录，不再自动重试
  it('tells an account without a password to sign in again', async () => {
    account.beginTwoFactorSetup.mockReturnValueOnce(
      throwError(() => failure('Auth:SignInAgainRequired')),
    );
    fixture.detectChanges();
    await fixture.whenStable();

    expect(host().querySelector('[data-testid="two-factor-setup-sign-in-again"]')).not.toBeNull();
    expect(account.beginTwoFactorSetup).toHaveBeenCalledOnce();
  });

  // 换绑：随开始设置出示现有的第二因素；不对时交回调用方重新输入
  it('sends the replacement proof and hands a rejected one back', async () => {
    fixture.componentRef.setInput('proof', { recoveryCode: 'AAAA-BBBB' });
    account.beginTwoFactorSetup.mockReturnValueOnce(
      throwError(() => failure('Auth:TwoFactorCodeInvalid')),
    );
    const rejected = vi.fn();
    fixture.componentInstance.proofRejected.subscribe(rejected);

    fixture.detectChanges();
    await fixture.whenStable();

    expect(account.beginTwoFactorSetup).toHaveBeenCalledWith({
      recoveryCode: 'AAAA-BBBB',
      password: undefined,
    });
    expect(rejected).toHaveBeenCalledOnce();
  });

  function submitButton(): HTMLButtonElement {
    return host().querySelector<HTMLButtonElement>('[data-testid="two-factor-enable"]')!;
  }

  async function typeCode(code: string): Promise<void> {
    const input = host().querySelector<HTMLInputElement>('input#two-factor-setup-code')!;
    input.value = code;
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    await fixture.whenStable();
  }

  function failure(code: string): ApplicationHttpError {
    return ApplicationHttpError.from(
      new HttpErrorResponse({ status: 400, error: { code, detail: code } }),
    );
  }
});

/** 组件里生成二维码的受保护方法，测试据它控制二维码何时生成完。 */
interface QrRenderer {
  renderQrCode(otpAuthUri: string): Promise<string>;
}
