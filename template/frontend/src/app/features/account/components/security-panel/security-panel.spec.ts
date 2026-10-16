import { HttpErrorResponse } from '@angular/common/http';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { toast } from '@spartan-ng/brain/sonner';
import { EMPTY, Observable, of, Subject, throwError } from 'rxjs';

import { SecurityPanel } from './security-panel';
import { ApplicationHttpError } from '../../../../core/errors/application-http-error';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../../../core/i18n/transloco.testing';
//#endif
import { AuthService } from '../../../../core/services/auth-service';
import { SettingContextService } from '../../../../core/settings/setting-context-service';
import { PASSWORD_MIN_LENGTH } from '../../../../core/validation/password-rule';
import { ChangePasswordInputDto } from '../../dtos/account.dto';
import { AccountService } from '../../services/account-service';

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
    getTwoFactorStatus: Mock<() => Observable<never>>;
    //#if (ExternalLogin)
    getExternalLogins: Mock<() => Observable<never>>;
    //#endif
  };

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
      //#endif
    };
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
        { provide: AuthService, useValue: { currentUser: signal(null), loadUser: () => EMPTY } },
        {
          provide: SettingContextService,
          useValue: { timeZone: signal(undefined), displayLocale: signal(undefined) },
        },
      ],
    });

    fixture = TestBed.createComponent(SecurityPanel);
    panel = fixture.componentInstance;
    await fixture.whenStable();
  });

  afterEach(() => fixture.destroy());

  it('associates every password label with its input', () => {
    for (const id of ['current-password', 'new-password', 'confirm-password']) {
      expect((fixture.nativeElement as HTMLElement).querySelector(`label[for="${id}"]`)).not.toBe(
        null,
      );
      expect(input(id)).not.toBeNull();
    }
  });

  // 横排时规则说明在左侧标签下、错误在输入框下：二者仍归同一个 Field，输入框的描述关联同时指向它们
  it('describes the new password by its rules and, while shown, by its error', async () => {
    const field = input('new-password').closest('[data-slot="field"]')!;
    const rules = field.querySelector<HTMLElement>('[data-slot="field-description"]')!;
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
});
