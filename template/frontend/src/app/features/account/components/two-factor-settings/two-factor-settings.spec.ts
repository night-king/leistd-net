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
});
