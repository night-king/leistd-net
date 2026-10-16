import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of } from 'rxjs';

import { TwoFactorSettings } from './two-factor-settings';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../../../core/i18n/transloco.testing';
//#endif
import { AuthService } from '../../../../core/services/auth-service';
import { TwoFactorStatusOutputDto } from '../../../../shared/dtos/account-security.dto';
import { AccountService } from '../../services/account-service';

import type { MockedObject } from 'vitest';

describe('TwoFactorSettings', () => {
  let fixture: ComponentFixture<TwoFactorSettings>;
  let account: Pick<
    MockedObject<AccountService>,
    'getTwoFactorStatus' | 'beginTwoFactorSetup' | 'disableTwoFactor'
  >;
  /** 是否设有口令取自当前用户：没有口令的账号停用时只填验证码。 */
  const currentUser = signal<unknown>({ hasPassword: true, isTwoFactorEnabled: true });

  async function render(status: TwoFactorStatusOutputDto): Promise<HTMLElement> {
    account.getTwoFactorStatus.mockReturnValue(of(status));
    fixture = TestBed.createComponent(TwoFactorSettings);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  beforeEach(() => {
    account = {
      getTwoFactorStatus: vi.fn().mockName('AccountService.getTwoFactorStatus'),
      beginTwoFactorSetup: vi.fn().mockName('AccountService.beginTwoFactorSetup'),
      disableTwoFactor: vi.fn().mockName('AccountService.disableTwoFactor'),
    };
    TestBed.configureTestingModule({
      imports: [TwoFactorSettings],
      providers: [
        { provide: AccountService, useValue: account },
        {
          provide: AuthService,
          useValue: { currentUser, loadUser: () => of(undefined), signInAgain: vi.fn() },
        },
        //#if (IncludeLocalization)
        ...provideTranslocoTesting(['en']),
        //#endif
      ],
    });
  });

  it('offers enable but not disable when not enabled', async () => {
    const host = await render({ enabled: false, recoveryCodesLeft: 0, requiredByPolicy: false });

    expect(host.querySelector('[data-testid="two-factor-turn-on"]')).not.toBeNull();
    expect(host.querySelector('[data-testid="two-factor-turn-off"]')).toBeNull();
  });

  it('offers no disable entry when the organization requires two-factor', async () => {
    const host = await render({ enabled: true, recoveryCodesLeft: 8, requiredByPolicy: true });

    expect(host.querySelector('[data-testid="two-factor-on"]')).not.toBeNull();
    expect(host.querySelector('[data-testid="two-factor-turn-off"]')).toBeNull();
    expect(host.querySelector('[data-testid="recovery-codes-left"]')).not.toBeNull();
  });

  // 验证器丢了：用一个恢复码开始换绑，不经过"停用"，组织强制两步验证时同样可用
  it('replaces a lost authenticator with a recovery code even when two-factor is required', async () => {
    account.beginTwoFactorSetup.mockReturnValue(
      of({
        setupId: 'setup-1',
        secret: 'JBSWY3DPEHPK3PXP',
        otpAuthUri: 'otpauth://totp/App:alice',
      }),
    );
    const host = await render({ enabled: true, recoveryCodesLeft: 8, requiredByPolicy: true });

    host.querySelector<HTMLButtonElement>('[data-testid="two-factor-replace"]')!.click();
    fixture.detectChanges();
    host.querySelector<HTMLButtonElement>('[data-testid="two-factor-replace-toggle"]')!.click();
    fixture.detectChanges();
    const input = host.querySelector<HTMLInputElement>(
      '[data-testid="two-factor-replace-recovery-code"]',
    )!;
    input.value = ' AAAA-BBBB ';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    host
      .querySelector('[data-testid="two-factor-replace-form"]')!
      .dispatchEvent(new Event('submit'));
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(account.beginTwoFactorSetup).toHaveBeenCalledWith({
      recoveryCode: 'AAAA-BBBB',
      password: undefined,
    });
    expect(host.querySelector('[data-testid="two-factor-secret"]')).not.toBeNull();
  });

  // 恢复码不多：提醒里带"重新生成"，操作行里不再重复同名按钮；够用时只在操作行里
  it('warns when few recovery codes are left and offers regenerating only there', async () => {
    let host = await render({ enabled: true, recoveryCodesLeft: 3, requiredByPolicy: false });

    expect(host.querySelector('[data-testid="recovery-codes-low"]')).not.toBeNull();
    expect(host.querySelector('[data-testid="recovery-codes-low-regenerate"]')).not.toBeNull();
    expect(host.querySelector('[data-testid="two-factor-regenerate"]')).toBeNull();

    host.querySelector<HTMLButtonElement>('[data-testid="recovery-codes-low-regenerate"]')!.click();
    fixture.detectChanges();
    expect(host.querySelector('[data-testid="two-factor-regenerate-form"]')).not.toBeNull();

    fixture.destroy();
    host = await render({ enabled: true, recoveryCodesLeft: 4, requiredByPolicy: false });

    expect(host.querySelector('[data-testid="recovery-codes-low"]')).toBeNull();
    expect(host.querySelector('[data-testid="two-factor-regenerate"]')).not.toBeNull();
  });

  // 换绑改用恢复码：可见标签"恢复码"，切换后焦点落在恢复码输入框
  it('labels the recovery code input and focuses it after switching', async () => {
    const host = await render({ enabled: true, recoveryCodesLeft: 8, requiredByPolicy: false });
    host.querySelector<HTMLButtonElement>('[data-testid="two-factor-replace"]')!.click();
    fixture.detectChanges();

    host.querySelector<HTMLButtonElement>('[data-testid="two-factor-replace-toggle"]')!.click();
    await fixture.whenStable();
    await new Promise((resolve) => requestAnimationFrame(resolve));

    const input = host.querySelector<HTMLInputElement>('#two-factor-replace-recovery-code')!;
    expect(host.querySelector('label[for="two-factor-replace-recovery-code"]')).not.toBeNull();
    expect(document.activeElement).toBe(input);
  });

  describe('turning it off', () => {
    afterEach(() => currentUser.set({ hasPassword: true, isTwoFactorEnabled: true }));

    async function openDisableForm(): Promise<HTMLElement> {
      const host = await render({ enabled: true, recoveryCodesLeft: 8, requiredByPolicy: false });
      host.querySelector<HTMLButtonElement>('[data-testid="two-factor-turn-off"]')!.click();
      fixture.detectChanges();
      return host;
    }

    async function typeCode(host: HTMLElement): Promise<void> {
      const input = host.querySelector<HTMLInputElement>('#two-factor-disable-code')!;
      input.value = '123456';
      input.dispatchEvent(new Event('input'));
      await fixture.whenStable();
    }

    it('asks for the password and the code, both with visible labels', async () => {
      account.disableTwoFactor.mockReturnValue(of(undefined));
      const host = await openDisableForm();

      expect(host.querySelector('label[for="two-factor-disable-password"]')).not.toBeNull();
      expect(host.querySelector('label[for="two-factor-disable-code"]')).not.toBeNull();
      const password = host.querySelector<HTMLInputElement>('#two-factor-disable-password')!;
      password.value = 'current-password';
      password.dispatchEvent(new Event('input'));
      await typeCode(host);
      host.querySelector<HTMLButtonElement>('[data-testid="two-factor-disable-submit"]')!.click();
      await fixture.whenStable();

      expect(account.disableTwoFactor).toHaveBeenCalledExactlyOnceWith({
        password: 'current-password',
        code: '123456',
      });
    });

    // 没有口令的账号只填验证码，请求里也不带口令（带了服务端按"方式不具备"拒绝）
    it('asks an account without a password for the code only', async () => {
      currentUser.set({ hasPassword: false, isTwoFactorEnabled: true });
      account.disableTwoFactor.mockReturnValue(of(undefined));
      const host = await openDisableForm();

      expect(host.querySelector('#two-factor-disable-password')).toBeNull();
      const submit = host.querySelector<HTMLButtonElement>(
        '[data-testid="two-factor-disable-submit"]',
      )!;
      expect(submit.disabled).toBe(true);

      await typeCode(host);
      expect(submit.disabled).toBe(false);
      submit.click();
      await fixture.whenStable();

      expect(account.disableTwoFactor).toHaveBeenCalledExactlyOnceWith({
        password: undefined,
        code: '123456',
      });
    });
  });
});
