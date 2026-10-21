import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of } from 'rxjs';

import { LoginDevices } from './login-devices';
import { ConfirmService } from '../../../../core/feedback/confirm-service';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../../../core/i18n/transloco.testing';
//#endif
import { SettingContextService } from '../../../../core/settings/setting-context-service';
import { UserSessionOutputDto } from '../../../../shared/dtos/account-security.dto';
import { AccountService } from '../../services/account-service';

import type { MockedObject } from 'vitest';

function session(overrides: Partial<UserSessionOutputDto>): UserSessionOutputDto {
  return {
    id: 'current',
    creationTime: '2026-09-18T01:00:00Z',
    lastSeenTime: '2026-09-18T02:00:00Z',
    ipAddress: '127.0.0.1',
    userAgent:
      'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36',
    isCurrent: true,
    ...overrides,
  };
}

describe('LoginDevices', () => {
  let fixture: ComponentFixture<LoginDevices>;
  let account: Pick<
    MockedObject<AccountService>,
    'getSessions' | 'revokeSession' | 'revokeOtherSessions'
  >;
  let confirmed: boolean;

  async function render(sessions: UserSessionOutputDto[]): Promise<HTMLElement> {
    account.getSessions.mockReturnValue(of(sessions));
    fixture = TestBed.createComponent(LoginDevices);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  beforeEach(() => {
    account = {
      getSessions: vi.fn().mockName('AccountService.getSessions'),
      revokeSession: vi.fn().mockName('AccountService.revokeSession'),
      revokeOtherSessions: vi.fn().mockName('AccountService.revokeOtherSessions'),
    };
    confirmed = true;

    TestBed.configureTestingModule({
      imports: [LoginDevices],
      providers: [
        { provide: AccountService, useValue: account },
        { provide: ConfirmService, useValue: { open: () => Promise.resolve(confirmed) } },
        {
          provide: SettingContextService,
          useValue: { timeZone: signal<string | undefined>('UTC'), displayLocale: signal('en') },
        },
        //#if (IncludeLocalization)
        ...provideTranslocoTesting(['en']),
        //#endif
      ],
    });
  });

  it('marks the current device with no sign-out button; other devices can sign out', async () => {
    const host = await render([
      session({}),
      session({
        id: 'phone',
        isCurrent: false,
        userAgent:
          'Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1',
      }),
    ]);

    const rows = host.querySelectorAll('[data-testid="session-row"]');
    expect(rows.length).toBe(2);
    expect(rows[0].querySelector('[data-testid="current-session"]')).not.toBeNull();
    expect(rows[0].querySelector('[data-testid="revoke-session"]')).toBeNull();
    expect(rows[0].textContent).toContain('Chrome 128 · macOS');
    expect(rows[1].querySelector('[data-testid="revoke-session"]')).not.toBeNull();
    expect(rows[1].textContent).toContain('Safari 17 · iOS');
  });

  it('hides "sign out all other devices" when only the current device remains', async () => {
    const host = await render([session({})]);

    expect(host.querySelector('[data-testid="revoke-other-sessions"]')).toBeNull();
  });

  it('revokes and removes a device on confirm, and sends nothing on cancel', async () => {
    account.revokeSession.mockReturnValue(of(undefined));
    const host = await render([session({}), session({ id: 'phone', isCurrent: false })]);

    confirmed = false;
    (host.querySelector('[data-testid="revoke-session"]') as HTMLButtonElement).click();
    await fixture.whenStable();
    expect(account.revokeSession).not.toHaveBeenCalled();

    confirmed = true;
    (host.querySelector('[data-testid="revoke-session"]') as HTMLButtonElement).click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(account.revokeSession).toHaveBeenCalledTimes(1);

    expect(account.revokeSession).toHaveBeenCalledWith('phone');
    expect(host.querySelectorAll('[data-testid="session-row"]').length).toBe(1);
  });

  it('leaves only the current device after signing out all others', async () => {
    account.revokeOtherSessions.mockReturnValue(of(2));
    const host = await render([
      session({}),
      session({ id: 'a', isCurrent: false }),
      session({ id: 'b', isCurrent: false }),
    ]);

    (host.querySelector('[data-testid="revoke-other-sessions"]') as HTMLButtonElement).click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(account.revokeOtherSessions).toHaveBeenCalledTimes(1);
    expect(host.querySelectorAll('[data-testid="session-row"]').length).toBe(1);
  });
});
