import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Observable, of, Subject, throwError } from 'rxjs';

import { ExternalLogins } from './external-logins';
import { ConfirmService } from '../../../../core/feedback/confirm-service';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../../../core/i18n/transloco.testing';
//#endif
import { SettingContextService } from '../../../../core/settings/setting-context-service';
import { ExternalLoginsOutputDto } from '../../dtos/account.dto';
import { AccountService } from '../../services/account-service';

import type { MockedObject } from 'vitest';

describe('ExternalLogins', () => {
  let fixture: ComponentFixture<ExternalLogins>;
  let account: Pick<
    MockedObject<AccountService>,
    'getExternalLogins' | 'unlinkExternalLogin' | 'getExternalLinkUrl'
  >;

  const github = {
    provider: 'github',
    link: { id: 'l1', providerAccountLabel: 'octocat', creationTime: '2026-06-01T00:00:00Z' },
  };

  async function render(
    data: ExternalLoginsOutputDto | Observable<ExternalLoginsOutputDto>,
  ): Promise<HTMLElement> {
    account.getExternalLogins.mockReturnValue(data instanceof Observable ? data : of(data));
    fixture = TestBed.createComponent(ExternalLogins);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  beforeEach(() => {
    account = {
      getExternalLogins: vi.fn().mockName('AccountService.getExternalLogins'),
      unlinkExternalLogin: vi.fn().mockName('AccountService.unlinkExternalLogin'),
      getExternalLinkUrl: vi.fn().mockName('AccountService.getExternalLinkUrl'),
    };
    TestBed.configureTestingModule({
      imports: [ExternalLogins],
      providers: [
        { provide: AccountService, useValue: account },
        { provide: ConfirmService, useValue: { open: () => Promise.resolve(true) } },
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

  it('offers link for unlinked providers and unlink for linked ones', async () => {
    const host = await render({
      hasPassword: true,
      providers: [github, { provider: 'google', link: null }],
    });

    const rows = host.querySelectorAll('[data-testid="external-login-row"]');
    expect(rows[0].querySelector('[data-testid="external-login-unlink"]')).not.toBeNull();
    expect(rows[1].querySelector('[data-testid="external-login-link"]')).not.toBeNull();
  });

  it('offers no unlink for the last linked provider when there is no password', async () => {
    const host = await render({
      hasPassword: false,
      providers: [github, { provider: 'google', link: null }],
    });

    expect(host.querySelector('[data-testid="external-login-unlink"]')).toBeNull();
  });

  // 部署没有可用的外部登录：标题、说明与外框都不出现，否则账户与安全里留着一块无事可做的空壳
  it('renders nothing once an empty provider directory has loaded', async () => {
    const host = await render({ hasPassword: true, providers: [] });

    expect(host.querySelector('[data-testid="security-external-logins"]')).toBeNull();
    expect(host.textContent?.trim()).toBe('');
  });

  // 还没拿到目录时不能当成"没有"：加载中照常显示区块
  it('keeps the section while the directory is loading', async () => {
    const host = await render(new Subject<ExternalLoginsOutputDto>());

    expect(host.querySelector('[data-testid="security-external-logins"]')).not.toBeNull();
    expect(host.querySelector('hlm-spinner')).not.toBeNull();
  });

  // 失败不是"没有"：区块留着、说出失败并能重试，重试拿到目录后恢复正常显示
  it('shows a retryable failure and recovers the section on retry', async () => {
    const host = await render(throwError(() => new Error('offline')));

    expect(host.querySelector('[data-testid="security-external-logins"]')).not.toBeNull();
    expect(host.querySelector('[data-testid="external-login-row"]')).toBeNull();
    const retry = host.querySelector<HTMLButtonElement>('[data-testid="external-logins-retry"]');
    expect(retry).not.toBeNull();

    account.getExternalLogins.mockReturnValue(
      of({ hasPassword: true, providers: [{ provider: 'google', link: null }] }),
    );
    retry!.click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(account.getExternalLogins).toHaveBeenCalledTimes(2);
    expect(host.querySelector('[data-testid="external-logins-retry"]')).toBeNull();
    expect(host.querySelectorAll('[data-testid="external-login-row"]')).toHaveLength(1);
  });
});
