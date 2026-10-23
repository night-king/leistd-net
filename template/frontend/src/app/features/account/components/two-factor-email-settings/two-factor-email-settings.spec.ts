import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { toast } from '@spartan-ng/brain/sonner';
import { of } from 'rxjs';

import { TwoFactorEmailSettings } from './two-factor-email-settings';
import { ConfirmService } from '../../../../core/feedback/confirm-service';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../../../core/i18n/transloco.testing';
//#endif
import { AuthService } from '../../../../core/services/auth-service';
import { TwoFactorStatusOutputDto } from '../../../../shared/dtos/account-security.dto';
import { AccountService } from '../../services/account-service';

import type { MockedObject } from 'vitest';

/** 邮箱验证码一节：用不了时只给原因；开启要确认码；两种方式都能用时才可选先用哪一种。 */
describe('TwoFactorEmailSettings', () => {
  let fixture: ComponentFixture<TwoFactorEmailSettings>;
  let account: Pick<
    MockedObject<AccountService>,
    | 'getTwoFactorStatus'
    | 'beginEmailTwoFactorSetup'
    | 'enableEmailTwoFactor'
    | 'disableEmailTwoFactor'
    | 'setPreferredTwoFactorMethod'
  >;
  let confirm: { open: ReturnType<typeof vi.fn> };

  const OFF: TwoFactorStatusOutputDto = {
    enabled: true,
    recoveryCodesLeft: 8,
    requiredByPolicy: false,
    defaultMethod: 'authenticator',
    emailEnabled: false,
    maskedEmail: 'ali***@example.com',
  };

  async function render(status: TwoFactorStatusOutputDto): Promise<HTMLElement> {
    account.getTwoFactorStatus.mockReturnValue(of(status));
    fixture = TestBed.createComponent(TwoFactorEmailSettings);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  function byTestId(id: string): HTMLElement | null {
    return (fixture.nativeElement as HTMLElement).querySelector<HTMLElement>(
      `[data-testid="${id}"]`,
    );
  }

  beforeEach(() => {
    vi.spyOn(toast, 'success').mockImplementation(() => '');
    account = {
      getTwoFactorStatus: vi.fn().mockName('AccountService.getTwoFactorStatus'),
      beginEmailTwoFactorSetup: vi.fn().mockName('AccountService.beginEmailTwoFactorSetup'),
      enableEmailTwoFactor: vi.fn().mockName('AccountService.enableEmailTwoFactor'),
      disableEmailTwoFactor: vi.fn().mockName('AccountService.disableEmailTwoFactor'),
      setPreferredTwoFactorMethod: vi.fn().mockName('AccountService.setPreferredTwoFactorMethod'),
    };
    confirm = { open: vi.fn().mockResolvedValue(true) };
    TestBed.configureTestingModule({
      imports: [TwoFactorEmailSettings],
      providers: [
        { provide: AccountService, useValue: account },
        { provide: ConfirmService, useValue: confirm },
        {
          provide: AuthService,
          useValue: {
            currentUser: signal({ hasPassword: true, isTwoFactorEnabled: true }),
            loadUser: () => of(undefined),
            signInAgain: vi.fn(),
          },
        },
        { provide: Router, useValue: { navigateByUrl: vi.fn() } },
        //#if (IncludeLocalization)
        ...provideTranslocoTesting(['en']),
        //#endif
      ],
    });
  });

  it('gives the reason and no turn-on entry when the organization does not allow email codes', async () => {
    await render({ ...OFF, emailUnavailableReason: 'notAllowed' });

    expect(byTestId('two-factor-email-turn-on')).toBeNull();
    //#if (IncludeLocalization)
    expect(byTestId('two-factor-email-unavailable')?.textContent).toContain(
      'account.twoFactorEmail.unavailable.notAllowed',
    );
    //#else
    expect(byTestId('two-factor-email-unavailable')?.textContent).toContain(
      "Your organization doesn't allow email codes for signing in.",
    );
    //#endif
  });

  // 开启：先发确认码，输入后才开启；开启会退出其他设备，面板据此刷新设备列表
  it('turns email codes on only after the mailed confirmation code', async () => {
    let sessionsChanged = 0;
    account.beginEmailTwoFactorSetup.mockReturnValue(
      of({ challengeId: 'setup-1', expiresInSeconds: 300, retryAfterSeconds: 60 }),
    );
    account.enableEmailTwoFactor.mockReturnValue(of(undefined));
    await render(OFF);
    fixture.componentInstance.sessionsChanged.subscribe(() => sessionsChanged++);

    byTestId('two-factor-email-turn-on')!.click();
    fixture.detectChanges();
    expect(byTestId('two-factor-email-confirm-form')).not.toBeNull();
    expect(account.enableEmailTwoFactor).not.toHaveBeenCalled();

    account.getTwoFactorStatus.mockReturnValue(of({ ...OFF, emailEnabled: true }));
    const input = (fixture.nativeElement as HTMLElement).querySelector<HTMLInputElement>(
      '#two-factor-email-code',
    )!;
    input.value = '123456';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(account.enableEmailTwoFactor).toHaveBeenCalledExactlyOnceWith({
      challengeId: 'setup-1',
      code: '123456',
    });
    expect(sessionsChanged).toBe(1);
    expect(byTestId('two-factor-email-on')).not.toBeNull();
    expect(byTestId('two-factor-email-confirm-form')).toBeNull();
  });

  // 两种都能用时才标"默认"：默认是身份验证器时这一节给"设为默认"，点了即保存并通知面板刷新另一节
  it('marks the default only when both methods work and lets email become the default', async () => {
    let defaultChanged = 0;
    account.setPreferredTwoFactorMethod.mockReturnValue(of(undefined));
    await render({ ...OFF, emailUnavailableReason: 'notAllowed', emailEnabled: true });
    expect(byTestId('two-factor-email-set-default')).toBeNull();
    expect(byTestId('two-factor-email-default')).toBeNull();

    await render({ ...OFF, emailEnabled: true });
    fixture.componentInstance.defaultChanged.subscribe(() => defaultChanged++);
    expect(byTestId('two-factor-email-default')).toBeNull();
    account.getTwoFactorStatus.mockReturnValue(
      of({ ...OFF, emailEnabled: true, preferredMethod: 'email', defaultMethod: 'email' }),
    );

    byTestId('two-factor-email-set-default')!.click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(account.setPreferredTwoFactorMethod).toHaveBeenCalledExactlyOnceWith('email');
    expect(defaultChanged).toBe(1);
    expect(byTestId('two-factor-email-default')).not.toBeNull();
    expect(byTestId('two-factor-email-set-default')).toBeNull();
  });

  it('asks before turning email codes off', async () => {
    account.disableEmailTwoFactor.mockReturnValue(of(undefined));
    await render({ ...OFF, emailEnabled: true });

    byTestId('two-factor-email-turn-off')!.click();
    await fixture.whenStable();

    expect(confirm.open).toHaveBeenCalledTimes(1);
    expect(account.disableEmailTwoFactor).toHaveBeenCalledTimes(1);
  });
});
