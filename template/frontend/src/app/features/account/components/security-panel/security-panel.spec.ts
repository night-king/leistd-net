import { HttpErrorResponse } from '@angular/common/http';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
//#if (Email)
import { By } from '@angular/platform-browser';
//#endif
import { toast } from '@spartan-ng/brain/sonner';
import { EMPTY, Observable, of, Subject, throwError } from 'rxjs';

import { SecurityPanel } from './security-panel';
//#if (ExternalLogin)
import {
  confirmWithCode,
  reauthenticateClosed,
  reauthenticateForm,
} from '../../../../core/components/reauthenticate-dialog/reauthenticate-dialog.testing';
//#endif
import { ApplicationHttpError } from '../../../../core/errors/application-http-error';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../../../core/i18n/transloco.testing';
//#endif
import { AccountPreferencesService } from '../../../../core/services/account-preferences-service';
import { AuthService } from '../../../../core/services/auth-service';
import { PASSWORD_MIN_LENGTH } from '../../../../core/validation/password-rule';
import { TwoFactorStatusOutputDto } from '../../../../shared/dtos/account-security.dto';
// prettier-ignore
import {
  ChangePasswordInputDto,
  //#if (ExternalLogin)
  SetPasswordInputDto,
  //#endif
} from '../../dtos/account.dto';
import { AccountService } from '../../services/account-service';
//#if (Email)
import { TwoFactorEmailSettings } from '../two-factor-email-settings/two-factor-email-settings';
//#endif

import type { Mock } from 'vitest';

/**
 * 「账户与安全」面板的修改口令表单。
 *
 * 面板不像弹窗那样一关就销毁：改密成功后旧口令与新口令必须清掉；失败时则要保留输入让人改，
 * 并把服务端给出的具体原因显示出来。服务端会让其他设备退出，设备列表随之刷新。
 */
describe('SecurityPanel', () => {
  let fixture: ComponentFixture<SecurityPanel>;
  let panel: SecurityPanel;
  let account: {
    changePassword: Mock<(input: ChangePasswordInputDto) => Observable<unknown>>;
    getSessions: Mock<() => Observable<unknown[]>>;
    getTwoFactorStatus: Mock<() => Observable<TwoFactorStatusOutputDto>>;
    //#if (ExternalLogin)
    getExternalLogins: Mock<() => Observable<never>>;
    setPassword: Mock<(input: SetPasswordInputDto) => Observable<unknown>>;
    //#endif
  };
  /** 当前用户：是否设有口令决定显示"修改密码"还是"设置密码"。 */
  const currentUser = signal<unknown>(null);
  let loadUser: Mock<() => Observable<never>>;

  type Field = 'currentPassword' | 'newPassword' | 'confirmPassword';

  function input(id: string): HTMLInputElement {
    return (fixture.nativeElement as HTMLElement).querySelector<HTMLInputElement>(`#${id}`)!;
  }

  function submitButton(): HTMLButtonElement {
    return (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>(
      '[data-testid="change-password-form"] button[type="submit"]',
    )!;
  }

  function errorKinds(field: Field): string[] {
    return panel.changeForm[field]()
      .errors()
      .map((error) => error.kind);
  }

  /** 显示出来的校验提示：未触碰的字段错误元素在，但处于 hidden。 */
  function shownErrorCount(): number {
    return Array.from(
      (fixture.nativeElement as HTMLElement).querySelectorAll<HTMLElement>('hlm-field-error'),
    ).filter((element) => !element.hidden).length;
  }

  async function fill(values: Partial<Record<Field, string>>): Promise<void> {
    for (const [field, value] of Object.entries(values)) {
      panel.changeForm[field as Field]().value.set(value);
    }
    await fixture.whenStable();
  }

  const valid = {
    currentPassword: 'Current!Passw0rd',
    newPassword: 'Changed!Passw0rd ',
    confirmPassword: 'Changed!Passw0rd ',
  };

  beforeEach(async () => {
    account = {
      changePassword: vi.fn().mockName('AccountService.changePassword'),
      getSessions: vi.fn().mockName('AccountService.getSessions'),
      // 同页的其他卡片只需能完成加载，内容与本表单无关
      getTwoFactorStatus: vi.fn(() => EMPTY),
      //#if (ExternalLogin)
      getExternalLogins: vi.fn(() => EMPTY),
      setPassword: vi.fn().mockName('AccountService.setPassword'),
      //#endif
    };
    loadUser = vi.fn(() => EMPTY);
    account.changePassword.mockReturnValue(of(undefined));
    account.getSessions.mockReturnValue(of([]));
    vi.spyOn(toast, 'success').mockImplementation(() => '');
    vi.spyOn(toast, 'error').mockImplementation(() => '');

    TestBed.configureTestingModule({
      imports: [SecurityPanel],
      // prettier-ignore
      providers: [
        //#if (IncludeLocalization)
        ...provideTranslocoTesting(['en']),
        //#endif
        { provide: AccountService, useValue: account },
        {
          provide: AuthService,
          useValue: { currentUser, loadUser, signInAgain: vi.fn() },
        },
        {
          provide: AccountPreferencesService,
          useValue: { timeZone: signal(undefined), displayLocale: signal(undefined) },
        },
      ],
    });

    fixture = TestBed.createComponent(SecurityPanel);
    panel = fixture.componentInstance;
    await fixture.whenStable();
  });

  afterEach(() => fixture.destroy());
  //#if (Email)

  // 邮箱启停会改变"两种方式都能用"：身份验证器一节的"默认 / 设为默认"随之刷新，不必离开再进
  it('refreshes the authenticator default mark when email codes are turned on', async () => {
    const status = (emailEnabled: boolean): TwoFactorStatusOutputDto => ({
      enabled: true,
      passkeys: [],
      passkeysAvailable: true,
      recoveryCodesLeft: 8,
      requiredByPolicy: false,
      emailEnabled,
      defaultMethod: 'authenticator',
    });
    account.getTwoFactorStatus.mockReturnValue(of(status(false)));
    fixture.destroy();
    fixture = TestBed.createComponent(SecurityPanel);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="two-factor-default"]')).toBeNull();

    account.getTwoFactorStatus.mockReturnValue(of(status(true)));
    (
      fixture.debugElement.query(By.directive(TwoFactorEmailSettings))
        .componentInstance as TwoFactorEmailSettings
    ).sessionsChanged.emit();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(host.querySelector('[data-testid="two-factor-default"]')).not.toBeNull();
  });
  //#endif

  it('associates every password label with its input', () => {
    for (const id of ['current-password', 'new-password', 'confirm-password']) {
      expect((fixture.nativeElement as HTMLElement).querySelector(`label[for="${id}"]`)).not.toBe(
        null,
      );
      expect(input(id)).not.toBeNull();
    }
  });

  // 规则不另写可见说明：输入框下方的长度进度条带一段只给读屏的进度描述，错误也在输入框下；
  // 二者归同一个 Field，输入框的描述关联同时指向它们
  it('describes the new password by its length progress and, while shown, by its error', async () => {
    const field = input('new-password').closest('[data-slot="field"]')!;
    const rules = field.querySelector<HTMLElement>('[data-slot="field-description"]')!;
    expect(rules.closest('app-password-length-meter')).not.toBeNull();
    expect(rules.querySelector('.sr-only')).not.toBeNull();
    const describedBy = () =>
      (input('new-password').getAttribute('aria-describedby') ?? '').split(/\s+/);
    expect(describedBy()).toContain(rules.id);

    submitButton().click();
    await fixture.whenStable();
    const error = Array.from(field.querySelectorAll<HTMLElement>('hlm-field-error')).find(
      (element) => !element.hidden,
    )!;
    expect(describedBy()).toEqual(expect.arrayContaining([rules.id, error.id]));

    await fill(valid);
    expect(describedBy()).toContain(rules.id);
    expect(describedBy()).not.toContain(error.id);
  });

  it('does not send an empty form and reveals the errors on submit', async () => {
    expect(shownErrorCount()).toBe(0);

    submitButton().click();
    await fixture.whenStable();

    expect(account.changePassword).not.toHaveBeenCalled();
    expect(errorKinds('currentPassword')).toEqual(['required']);
    expect(errorKinds('confirmPassword')).toEqual(['required']);
    expect(shownErrorCount()).toBeGreaterThan(0);
  });

  it('rejects a new password that is too short, equal to the current one, or not confirmed', async () => {
    await fill({ ...valid, newPassword: 'x'.repeat(PASSWORD_MIN_LENGTH - 1) });
    expect(errorKinds('newPassword')).toEqual(['minLength']);

    await fill({ ...valid, newPassword: valid.currentPassword });
    expect(errorKinds('newPassword')).toEqual(['passwordSameAsCurrent']);

    await fill({ ...valid, confirmPassword: 'Changed!Passw0rd' });
    expect(errorKinds('newPassword')).toEqual([]);
    expect(errorKinds('confirmPassword')).toEqual(['passwordMismatch']);

    submitButton().click();
    await fixture.whenStable();
    expect(account.changePassword).not.toHaveBeenCalled();
  });

  it('sends the passwords as typed, clears the form and reloads the sign-in devices', async () => {
    const sessionLoads = account.getSessions.mock.calls.length;
    await fill(valid);
    input('new-password').parentElement!.querySelector<HTMLButtonElement>('button')!.click();
    await fixture.whenStable();
    expect(input('new-password').type).toBe('text');

    submitButton().click();
    await fixture.whenStable();

    // 口令里的空白是口令的一部分，不能 trim
    expect(account.changePassword).toHaveBeenCalledExactlyOnceWith(valid);
    expect(toast.success).toHaveBeenCalledOnce();
    expect(panel.changeForm.currentPassword().value()).toBe('');
    expect(panel.changeForm.newPassword().value()).toBe('');
    expect(panel.changeForm.confirmPassword().value()).toBe('');
    // 清空后不能立刻把三个字段标成"必填"错误
    expect(shownErrorCount()).toBe(0);
    expect(input('new-password').type).toBe('password');
    // 服务端已让其他设备退出，设备列表随之刷新
    expect(account.getSessions).toHaveBeenCalledTimes(sessionLoads + 1);
    expect(panel.saving()).toBe(false);
  });

  it('blocks a second submit while the change is in flight', async () => {
    const response = new Subject<void>();
    account.changePassword.mockReturnValue(response);
    await fill(valid);

    submitButton().click();
    await fixture.whenStable();
    expect(submitButton().disabled).toBe(true);
    submitButton().click();
    await fixture.whenStable();

    expect(account.changePassword).toHaveBeenCalledOnce();

    response.next();
    response.complete();
    await fixture.whenStable();
    expect(submitButton().disabled).toBe(false);
  });

  it('keeps the input and shows the server reason when the change is rejected', async () => {
    const rejected = ApplicationHttpError.from(
      new HttpErrorResponse({
        status: 400,
        error: {
          errors: [{ field: 'currentPassword', detail: 'Current password is wrong.', code: 'X:Y' }],
        },
      }),
    );
    account.changePassword.mockReturnValue(throwError(() => rejected));
    const sessionLoads = account.getSessions.mock.calls.length;
    await fill(valid);

    submitButton().click();
    await fixture.whenStable();

    expect(toast.error).toHaveBeenCalledOnce();
    expect(vi.mocked(toast.error).mock.calls[0][1]).toEqual({
      description: 'Current password is wrong.',
    });
    expect(toast.success).not.toHaveBeenCalled();
    expect(panel.changeForm.currentPassword().value()).toBe(valid.currentPassword);
    expect(panel.changeForm.newPassword().value()).toBe(valid.newPassword);
    expect(account.getSessions).toHaveBeenCalledTimes(sessionLoads);
    expect(panel.saving()).toBe(false);
    expect(submitButton().disabled).toBe(false);
  });
  //#if (ExternalLogin)

  // 只经外部登录建立的账号：没有当前口令可填，"修改密码"换成"设置密码"
  describe('setting a first password', () => {
    const newPassword = 'First!Passw0rd ';

    beforeEach(async () => {
      currentUser.set({ hasPassword: false, isTwoFactorEnabled: true });
      await fixture.whenStable();
    });

    afterEach(() => currentUser.set(null));

    async function submitNewPassword(): Promise<void> {
      panel.setForm.newPassword().value.set(newPassword);
      panel.setForm.confirmPassword().value.set(newPassword);
      await fixture.whenStable();
      (fixture.nativeElement as HTMLElement)
        .querySelector<HTMLButtonElement>(
          '[data-testid="set-password-form"] button[type="submit"]',
        )!
        .click();
      await fixture.whenStable();
    }

    it('replaces the change-password form with a set-password form', () => {
      const host = fixture.nativeElement as HTMLElement;

      expect(host.querySelector('[data-testid="security-password"]')).toBeNull();
      expect(host.querySelector('[data-testid="security-set-password"]')).not.toBeNull();
      expect(input('current-password')).toBeNull();
      expect(input('set-new-password')).not.toBeNull();
    });

    // 刚完成外部登录的会话不必确认身份；成功后刷新当前用户与外部绑定（有了口令，最后一个绑定也能解绑）
    it('sets the password without a prompt on a recent session and refreshes the bindings', async () => {
      account.setPassword.mockReturnValue(of(undefined));
      const bindingLoads = account.getExternalLogins.mock.calls.length;

      await submitNewPassword();

      expect(account.setPassword).toHaveBeenCalledExactlyOnceWith({
        newPassword,
        confirmPassword: newPassword,
        twoFactorCode: undefined,
      });
      expect(await reauthenticateClosed(fixture)).toBe(true);
      expect(loadUser).toHaveBeenCalled();
      expect(account.getExternalLogins).toHaveBeenCalledTimes(bindingLoads + 1);
    });

    it('confirms identity with the code when the session is not recent', async () => {
      account.setPassword
        .mockReturnValueOnce(
          throwError(() =>
            ApplicationHttpError.from(
              new HttpErrorResponse({
                status: 400,
                error: { code: 'Auth:ReauthenticationRequired' },
              }),
            ),
          ),
        )
        .mockReturnValueOnce(of(undefined));

      await submitNewPassword();
      expect(reauthenticateForm()).not.toBeNull();
      expect(toast.error).not.toHaveBeenCalled();

      await confirmWithCode(fixture, '123456');

      expect(account.setPassword).toHaveBeenLastCalledWith({
        newPassword,
        confirmPassword: newPassword,
        twoFactorCode: '123456',
      });
      expect(toast.success).toHaveBeenCalledOnce();
    });
  });
  //#endif
});
