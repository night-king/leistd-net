import { HttpErrorResponse } from '@angular/common/http';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { toast } from '@spartan-ng/brain/sonner';
import { of, throwError } from 'rxjs';

import { TwoFactorPasskeySettings } from './two-factor-passkey-settings';
import {
  confirmWithPassword,
  reauthenticateMethod,
} from '../../../../core/components/reauthenticate-dialog/reauthenticate-dialog.testing';
import { ApplicationHttpError } from '../../../../core/errors/application-http-error';
import { ConfirmService } from '../../../../core/feedback/confirm-service';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../../../core/i18n/transloco.testing';
//#endif
import { AuthService } from '../../../../core/services/auth-service';
import { SettingContextService } from '../../../../core/settings/setting-context-service';
import {
  CREATION_OPTIONS,
  fakeAssertion,
  fakeAttestation,
  REQUEST_OPTIONS,
  webAuthnError,
} from '../../../../core/webauthn/webauthn.testing';
import {
  PasskeyOutputDto,
  TwoFactorStatusOutputDto,
} from '../../../../shared/dtos/account-security.dto';
import { AccountService } from '../../services/account-service';

import type { MockedObject } from 'vitest';

const LAPTOP: PasskeyOutputDto = {
  id: 'passkey-1',
  name: 'macOS · Chrome',
  creationTime: '2026-10-01T08:00:00Z',
  lastUsedTime: '2026-10-20T08:00:00Z',
  isBackupEligible: true,
  isBackedUp: true,
};

const SECURITY_KEY: PasskeyOutputDto = {
  id: 'passkey-2',
  name: 'Security key',
  creationTime: '2026-10-02T08:00:00Z',
  lastUsedTime: null,
  isBackupEligible: false,
  isBackedUp: false,
};

/** 有身份验证器、还没有通行密钥。 */
const NONE: TwoFactorStatusOutputDto = {
  enabled: true,
  passkeys: [],
  passkeysAvailable: true,
  recoveryCodesLeft: 8,
  requiredByPolicy: false,
  defaultMethod: 'authenticator',
};

function rejection(code: string, status = 400): ApplicationHttpError {
  return ApplicationHttpError.from(
    new HttpErrorResponse({ status, error: { status, code, title: code, detail: code } }),
    { method: 'POST' },
  );
}

/** 通行密钥一节：列表、添加（确认身份之后再点一次）、改名、删除、不可用原因与只有通行密钥时的恢复码。 */
describe('TwoFactorPasskeySettings', () => {
  let fixture: ComponentFixture<TwoFactorPasskeySettings>;
  let account: Pick<
    MockedObject<AccountService>,
    | 'getTwoFactorStatus'
    | 'beginPasskeyRegistration'
    | 'completePasskeyRegistration'
    | 'renamePasskey'
    | 'removePasskey'
    | 'regenerateRecoveryCodes'
    | 'setPreferredTwoFactorMethod'
  >;
  let auth: {
    currentUser: ReturnType<typeof signal>;
    loadUser: ReturnType<typeof vi.fn>;
    beginPasskeyReauthentication: ReturnType<typeof vi.fn>;
    signInAgain: ReturnType<typeof vi.fn>;
  };
  let confirmed: boolean;

  async function render(status: TwoFactorStatusOutputDto): Promise<HTMLElement> {
    account.getTwoFactorStatus.mockReturnValue(of(status));
    fixture = TestBed.createComponent(TwoFactorPasskeySettings);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  async function settle(): Promise<void> {
    await fixture.whenStable();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  function byTestId(id: string): HTMLElement | null {
    return (fixture.nativeElement as HTMLElement).querySelector<HTMLElement>(
      `[data-testid="${id}"]`,
    );
  }

  function allByTestId(id: string): HTMLElement[] {
    return Array.from(
      (fixture.nativeElement as HTMLElement).querySelectorAll<HTMLElement>(`[data-testid="${id}"]`),
    );
  }

  beforeEach(() => {
    vi.spyOn(toast, 'success').mockImplementation(() => '');
    vi.spyOn(toast, 'info').mockImplementation(() => '');
    vi.spyOn(toast, 'error').mockImplementation(() => '');
    account = {
      getTwoFactorStatus: vi.fn().mockName('AccountService.getTwoFactorStatus'),
      beginPasskeyRegistration: vi
        .fn()
        .mockName('AccountService.beginPasskeyRegistration')
        .mockReturnValue(of({ options: CREATION_OPTIONS })),
      completePasskeyRegistration: vi.fn().mockName('AccountService.completePasskeyRegistration'),
      renamePasskey: vi.fn().mockName('AccountService.renamePasskey'),
      removePasskey: vi.fn().mockName('AccountService.removePasskey'),
      regenerateRecoveryCodes: vi.fn().mockName('AccountService.regenerateRecoveryCodes'),
      setPreferredTwoFactorMethod: vi.fn().mockName('AccountService.setPreferredTwoFactorMethod'),
    };
    auth = {
      currentUser: signal({ hasPassword: true, isTwoFactorEnabled: true, hasPasskeys: false }),
      loadUser: vi.fn().mockReturnValue(of(undefined)),
      beginPasskeyReauthentication: vi.fn().mockReturnValue(of({ options: REQUEST_OPTIONS })),
      signInAgain: vi.fn(),
    };
    confirmed = true;
    TestBed.configureTestingModule({
      imports: [TwoFactorPasskeySettings],
      providers: [
        { provide: AccountService, useValue: account },
        { provide: AuthService, useValue: auth },
        { provide: ConfirmService, useValue: { open: () => Promise.resolve(confirmed) } },
        {
          provide: SettingContextService,
          useValue: { timeZone: signal<string | undefined>('UTC'), displayLocale: signal('en') },
        },
        { provide: Router, useValue: { url: '/workspace/settings/security' } },
        //#if (IncludeLocalization)
        ...provideTranslocoTesting(['en']),
        //#endif
      ],
    });
  });
  afterEach(() => vi.restoreAllMocks());

  it('lists each passkey with its backup state and when it was last used', async () => {
    await render({ ...NONE, passkeys: [LAPTOP, SECURITY_KEY] });

    const rows = allByTestId('passkey-row');
    expect(
      rows.map((row) => row.querySelector('[data-testid="passkey-name"]')!.textContent),
    ).toEqual(['macOS · Chrome', 'Security key']);
    const backup = rows.map((row) =>
      row.querySelector('[data-testid="passkey-backup"]')!.textContent!.trim(),
    );
    //#if (IncludeLocalization)
    expect(backup).toEqual(['account.passkeys.synced', 'account.passkeys.deviceBound']);
    expect(rows[1]!.querySelector('[data-testid="passkey-meta"]')!.textContent).toContain(
      'account.passkeys.neverUsed',
    );
    //#else
    expect(backup).toEqual(['Synced', 'This device only']);
    expect(rows[1]!.querySelector('[data-testid="passkey-meta"]')!.textContent).toContain(
      'Never used',
    );
    //#endif
    expect(byTestId('passkeys-on')).not.toBeNull();
  });

  // 会话刚认证过：这次点击直接调起浏览器；第一把独立因子时服务端一并发下恢复码
  it('adds a passkey named after this device and shows the recovery codes it brings', async () => {
    const create = vi
      .spyOn(navigator.credentials, 'create')
      .mockResolvedValue(fakeAttestation('cred-new'));
    account.completePasskeyRegistration.mockReturnValue(
      of({ passkey: LAPTOP, recoveryCodes: ['code-1', 'code-2'] }),
    );
    await render({ ...NONE, enabled: false, recoveryCodesLeft: 0 });
    let sessionsChanged = 0;
    fixture.componentInstance.sessionsChanged.subscribe(() => sessionsChanged++);
    account.getTwoFactorStatus.mockReturnValue(
      of({ ...NONE, enabled: false, passkeys: [LAPTOP], recoveryCodesLeft: 2 }),
    );

    byTestId('passkeys-add')!.click();
    await settle();

    expect(account.beginPasskeyRegistration).toHaveBeenCalledExactlyOnceWith({
      password: undefined,
      twoFactorCode: undefined,
      passkey: undefined,
    });
    expect(create).toHaveBeenCalledTimes(1);
    const [{ credential, name }] = account.completePasskeyRegistration.mock.calls[0]!;
    expect(credential).toMatchObject({ id: 'cred-new' });
    expect(name.length).toBeGreaterThan(0);
    expect(
      (fixture.nativeElement as HTMLElement).querySelector('app-recovery-codes'),
    ).not.toBeNull();
    expect(auth.loadUser).toHaveBeenCalled();
    expect(sessionsChanged).toBe(1);
  });

  it('skips the recovery codes when the account already had them', async () => {
    vi.spyOn(navigator.credentials, 'create').mockResolvedValue(fakeAttestation());
    account.completePasskeyRegistration.mockReturnValue(of({ passkey: LAPTOP, recoveryCodes: [] }));
    await render(NONE);

    byTestId('passkeys-add')!.click();
    await settle();

    expect(account.completePasskeyRegistration).toHaveBeenCalledTimes(1);
    expect((fixture.nativeElement as HTMLElement).querySelector('app-recovery-codes')).toBeNull();
  });

  // 浏览器要求创建发生在用户手势里：确认身份之后停在"创建通行密钥"，本人再点一次才调起浏览器
  it('waits for another click after confirming identity before creating the passkey', async () => {
    const create = vi.spyOn(navigator.credentials, 'create').mockResolvedValue(fakeAttestation());
    account.completePasskeyRegistration.mockReturnValue(of({ passkey: LAPTOP, recoveryCodes: [] }));
    account.beginPasskeyRegistration
      .mockReturnValueOnce(throwError(() => rejection('Auth:ReauthenticationRequired')))
      .mockReturnValueOnce(of({ options: CREATION_OPTIONS }));
    await render(NONE);

    byTestId('passkeys-add')!.click();
    await settle();
    expect(reauthenticateMethod()).toBe('password');

    await confirmWithPassword(fixture, 'Admin@123456');
    await settle();

    expect(account.beginPasskeyRegistration).toHaveBeenLastCalledWith({
      password: 'Admin@123456',
      twoFactorCode: undefined,
      passkey: undefined,
    });
    expect(create).not.toHaveBeenCalled();
    expect(byTestId('passkeys-continue')).not.toBeNull();

    byTestId('passkeys-continue-submit')!.click();
    await settle();

    expect(create).toHaveBeenCalledTimes(1);
    expect(account.completePasskeyRegistration).toHaveBeenCalledTimes(1);
  });

  it('treats a cancelled browser prompt as a neutral note and registers nothing', async () => {
    vi.spyOn(navigator.credentials, 'create').mockRejectedValue(webAuthnError('NotAllowedError'));
    await render(NONE);

    byTestId('passkeys-add')!.click();
    await settle();

    expect(toast.info).toHaveBeenCalledTimes(1);
    expect(toast.error).not.toHaveBeenCalled();
    expect(account.completePasskeyRegistration).not.toHaveBeenCalled();
    expect((byTestId('passkeys-add') as HTMLButtonElement).disabled).toBe(false);
  });

  it('disables adding and says why when the deployment has no passkeys', async () => {
    await render({ ...NONE, passkeysAvailable: false });

    expect((byTestId('passkeys-add') as HTMLButtonElement).disabled).toBe(true);
    expect(byTestId('passkeys-unavailable')!.getAttribute('data-reason')).toBe('notConfigured');
  });

  it('renames a passkey in place', async () => {
    account.renamePasskey.mockReturnValue(of({ ...LAPTOP, name: 'Work laptop' }));
    await render({ ...NONE, passkeys: [LAPTOP] });

    byTestId('passkey-rename')!.click();
    await settle();
    const input = byTestId('passkey-rename-input') as HTMLInputElement;
    expect(input.value).toBe('macOS · Chrome');
    input.value = '  Work laptop ';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    byTestId('passkey-rename-save')!.click();
    await settle();

    expect(account.renamePasskey).toHaveBeenCalledExactlyOnceWith('passkey-1', 'Work laptop');
    expect(byTestId('passkey-rename-input')).toBeNull();
    expect(byTestId('passkey-name')!.textContent).toBe('Work laptop');
  });

  it('removes a passkey after confirmation and refreshes the other sections', async () => {
    account.removePasskey.mockReturnValue(of(undefined));
    await render({ ...NONE, passkeys: [LAPTOP, SECURITY_KEY] });
    let defaultChanged = 0;
    fixture.componentInstance.defaultChanged.subscribe(() => defaultChanged++);

    allByTestId('passkey-remove')[1]!.click();
    await settle();

    expect(account.removePasskey).toHaveBeenCalledExactlyOnceWith('passkey-2', {
      password: undefined,
      twoFactorCode: undefined,
      passkey: undefined,
    });
    expect(defaultChanged).toBe(1);
  });

  it('keeps the passkey when the removal is not confirmed', async () => {
    confirmed = false;
    await render({ ...NONE, passkeys: [LAPTOP] });

    byTestId('passkey-remove')!.click();
    await settle();

    expect(account.removePasskey).not.toHaveBeenCalled();
  });

  // 组织要求两步验证、它又是最后一个独立因子：不能删
  it('blocks removing the last independent factor the organization requires', async () => {
    await render({ ...NONE, enabled: false, requiredByPolicy: true, passkeys: [LAPTOP] });

    expect((byTestId('passkey-remove') as HTMLButtonElement).disabled).toBe(true);
    expect(byTestId('passkeys-removal-blocked')).not.toBeNull();
  });

  // 只有通行密钥：提示再加一把或身份验证器；恢复码在这一节，用通行密钥重新生成
  it('suggests a backup factor and regenerates recovery codes with a passkey', async () => {
    const get = vi.spyOn(navigator.credentials, 'get').mockResolvedValue(fakeAssertion('cred-1'));
    account.regenerateRecoveryCodes.mockReturnValue(of({ recoveryCodes: ['new-1'] }));
    await render({ ...NONE, enabled: false, passkeys: [LAPTOP], recoveryCodesLeft: 2 });

    expect(byTestId('passkeys-only-hint')).not.toBeNull();
    expect(byTestId('passkeys-recovery-codes-low')).not.toBeNull();

    byTestId('passkeys-regenerate')!.click();
    await settle();

    expect(auth.beginPasskeyReauthentication).toHaveBeenCalledTimes(1);
    expect(get).toHaveBeenCalledTimes(1);
    expect(account.regenerateRecoveryCodes).toHaveBeenCalledExactlyOnceWith({
      passkey: expect.objectContaining({ id: 'cred-1' }),
    });
    expect(
      (fixture.nativeElement as HTMLElement).querySelector('app-recovery-codes'),
    ).not.toBeNull();
  });

  it('marks the default and offers to make passkeys the default when another method exists', async () => {
    account.setPreferredTwoFactorMethod.mockReturnValue(of(undefined));
    await render({ ...NONE, passkeys: [LAPTOP], defaultMethod: 'authenticator' });

    expect(byTestId('passkeys-default')).toBeNull();
    byTestId('passkeys-set-default')!.click();
    await settle();

    expect(account.setPreferredTwoFactorMethod).toHaveBeenCalledExactlyOnceWith('passkey');
  });

  it('shows no default choice when passkeys are the only method', async () => {
    await render({ ...NONE, enabled: false, passkeys: [LAPTOP], defaultMethod: 'passkey' });

    expect(byTestId('passkeys-default')).toBeNull();
    expect(byTestId('passkeys-set-default')).toBeNull();
  });
});
