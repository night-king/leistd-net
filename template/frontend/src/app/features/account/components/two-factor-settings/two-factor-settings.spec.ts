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
  let account: Pick<MockedObject<AccountService>, 'getTwoFactorStatus' | 'beginTwoFactorSetup'>;

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
    };
    TestBed.configureTestingModule({
      imports: [TwoFactorSettings],
      providers: [
        { provide: AccountService, useValue: account },
        { provide: AuthService, useValue: { loadUser: () => of(undefined) } },
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
});
