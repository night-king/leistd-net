import { HttpErrorResponse } from '@angular/common/http';
import { signal, WritableSignal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { toast } from '@spartan-ng/brain/sonner';
import { of, throwError } from 'rxjs';

import { ProfilePanel } from './profile-panel';
import {
  confirmWithCode,
  confirmWithPassword,
  reauthenticateClosed,
  reauthenticateErrorText,
  reauthenticateForm,
} from '../../../../core/components/reauthenticate-dialog/reauthenticate-dialog.testing';
import { ApplicationHttpError } from '../../../../core/errors/application-http-error';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../../../core/i18n/transloco.testing';
//#endif
import { AuthService } from '../../../../core/services/auth-service';
//#if (Email)
import { SecurityConfigService } from '../../../../core/services/security-config-service';
//#endif
import { AccountService } from '../../services/account-service';

import type { Mock } from 'vitest';

/** 个人资料面板：资料类写请求成功后由本页把服务端返回的资料写回当前用户，失败时不写回。 */
describe('ProfilePanel', () => {
  const initialUser = {
    id: 'user-1',
    username: 'alice',
    email: 'alice@example.com',
    displayName: 'Alice',
    avatar: '/api/v1/users/user-1/avatar?v=1',
    isEmailVerified: false,
    roles: [],
    isSuperAdmin: false,
    hasPassword: true,
    isTwoFactorEnabled: true,
  };

  let fixture: ComponentFixture<ProfilePanel>;
  let currentUser: WritableSignal<unknown>;
  let setCurrentUser: Mock;
  let account: {
    updateCurrentUser: Mock;
    setAvatar: Mock;
    //#if (Email)
    confirmCurrentEmail: Mock;
    //#endif
  };

  beforeEach(async () => {
    currentUser = signal<unknown>(initialUser);
    setCurrentUser = vi.fn((user: unknown) => currentUser.set(user));
    account = {
      updateCurrentUser: vi.fn(),
      setAvatar: vi.fn(),
      //#if (Email)
      confirmCurrentEmail: vi.fn(),
      //#endif
    };
    vi.spyOn(toast, 'success').mockImplementation(() => '');
    vi.spyOn(toast, 'error').mockImplementation(() => '');

    TestBed.configureTestingModule({
      imports: [ProfilePanel],
      providers: [
        //#if (IncludeLocalization)
        ...provideTranslocoTesting(['en']),
        //#endif
        {
          provide: AuthService,
          useValue: { currentUser, setCurrentUser, loadUser: vi.fn(), signInAgain: vi.fn() },
        },
        { provide: AccountService, useValue: account },
        //#if (Email)
        {
          provide: SecurityConfigService,
          useValue: {
            config: signal({
              registrationEnabled: true,
              enableEmailVerification: true,
              emailVerificationAvailable: true,
            }),
            refresh: vi.fn(),
          },
        },
        //#endif
      ],
    });
    fixture = TestBed.createComponent(ProfilePanel);
    await fixture.whenStable();
  });

  function failure() {
    return throwError(
      () => new HttpErrorResponse({ status: 400, statusText: 'Bad Request', error: {} }),
    );
  }

  it('writes the saved profile back to the current user and refills the form from it', async () => {
    const saved = { ...initialUser, displayName: 'Alice Smith' };
    account.updateCurrentUser.mockReturnValue(of(saved));
    const panel = fixture.componentInstance;
    panel.profileForm.displayName().value.set('  Alice Smith  ');

    panel.onSubmit();
    await fixture.whenStable();

    expect(account.updateCurrentUser).toHaveBeenCalledExactlyOnceWith({
      username: 'alice',
      email: 'alice@example.com',
      displayName: 'Alice Smith',
      phoneNumber: undefined,
    });
    expect(setCurrentUser).toHaveBeenCalledExactlyOnceWith(saved);
    expect(panel.profileForm.displayName().value()).toBe('Alice Smith');
    expect(panel.saving()).toBe(false);
  });

  function rejection(code: string, detail = code) {
    return throwError(() =>
      ApplicationHttpError.from(new HttpErrorResponse({ status: 400, error: { code, detail } })),
    );
  }

  // 换邮箱而会话不够新：先不带凭证提交，服务端要求时才弹出确认身份，带上验证码重试同一次保存
  it('asks to confirm identity only when required and retries the save with the code', async () => {
    const saved = { ...initialUser, email: 'alice@new.example.com' };
    account.updateCurrentUser
      .mockReturnValueOnce(rejection('Auth:ReauthenticationRequired'))
      .mockReturnValueOnce(of(saved));
    const panel = fixture.componentInstance;
    panel.profileForm.email().value.set('alice@new.example.com');

    expect(await reauthenticateClosed(fixture)).toBe(true);
    panel.onSubmit();
    await fixture.whenStable();

    expect(account.updateCurrentUser.mock.calls[0][0].currentPassword).toBeUndefined();
    expect(account.updateCurrentUser.mock.calls[0][0].twoFactorCode).toBeUndefined();
    expect(reauthenticateForm()).not.toBeNull();
    expect(setCurrentUser).not.toHaveBeenCalled();
    expect(toast.error).not.toHaveBeenCalled();

    await confirmWithCode(fixture, '123456');

    expect(account.updateCurrentUser).toHaveBeenCalledTimes(2);
    const retried = account.updateCurrentUser.mock.calls[1][0];
    expect(retried).toMatchObject({ email: 'alice@new.example.com', twoFactorCode: '123456' });
    expect(retried.currentPassword).toBeUndefined();
    expect(setCurrentUser).toHaveBeenCalledExactlyOnceWith(saved);
    expect(await reauthenticateClosed(fixture)).toBe(true);
  });

  it('keeps a wrong password inside the dialog and retries with the next one', async () => {
    const saved = { ...initialUser, email: 'alice@new.example.com' };
    account.updateCurrentUser
      .mockReturnValueOnce(rejection('Auth:ReauthenticationRequired'))
      .mockReturnValueOnce(
        rejection('Security:CurrentPasswordIncorrect', 'The current password is incorrect.'),
      )
      .mockReturnValueOnce(of(saved));
    const panel = fixture.componentInstance;
    panel.profileForm.email().value.set('alice@new.example.com');
    panel.onSubmit();
    await fixture.whenStable();

    await confirmWithPassword(fixture, 'wrong-password');

    expect(account.updateCurrentUser.mock.calls[1][0]).toMatchObject({
      currentPassword: 'wrong-password',
    });
    expect(reauthenticateErrorText()).toBe('The current password is incorrect.');
    expect(toast.error).not.toHaveBeenCalled();

    await confirmWithPassword(fixture, 'current-password');

    expect(account.updateCurrentUser.mock.calls[2][0]).toMatchObject({
      currentPassword: 'current-password',
    });
    expect(account.updateCurrentUser.mock.calls[2][0].twoFactorCode).toBeUndefined();
    expect(setCurrentUser).toHaveBeenCalledExactlyOnceWith(saved);
  });

  it('leaves the current user untouched when saving the profile fails', async () => {
    account.updateCurrentUser.mockReturnValue(failure());
    const panel = fixture.componentInstance;
    panel.profileForm.displayName().value.set('Alice Smith');

    panel.onSubmit();
    await fixture.whenStable();

    expect(setCurrentUser).not.toHaveBeenCalled();
    expect(currentUser()).toBe(initialUser);
    expect(toast.error).toHaveBeenCalledOnce();
    expect(panel.saving()).toBe(false);
  });

  it('writes the profile with the cleared avatar back to the current user', async () => {
    const saved = { ...initialUser, avatar: undefined };
    account.setAvatar.mockReturnValue(of(saved));

    fixture.componentInstance.removeAvatar();
    await fixture.whenStable();

    expect(account.setAvatar).toHaveBeenCalledExactlyOnceWith({ avatar: null });
    expect(setCurrentUser).toHaveBeenCalledExactlyOnceWith(saved);
    expect(fixture.componentInstance.avatarBusy()).toBe(false);
  });

  it('leaves the current user untouched when changing the avatar fails', async () => {
    account.setAvatar.mockReturnValue(failure());

    fixture.componentInstance.removeAvatar();
    await fixture.whenStable();

    expect(setCurrentUser).not.toHaveBeenCalled();
    expect(currentUser()).toBe(initialUser);
    expect(fixture.componentInstance.avatarBusy()).toBe(false);
  });
  //#if (Email)

  it('writes the verified profile back to the current user and closes the challenge', async () => {
    const saved = { ...initialUser, isEmailVerified: true };
    account.confirmCurrentEmail.mockReturnValue(of(saved));
    const panel = fixture.componentInstance;
    panel.emailChallenge.set({ challengeId: 'challenge-1', email: initialUser.email });
    panel.emailCode.set(' 123456 ');

    panel.confirmEmailCode();
    await fixture.whenStable();

    expect(account.confirmCurrentEmail).toHaveBeenCalledExactlyOnceWith({
      challengeId: 'challenge-1',
      code: '123456',
    });
    expect(setCurrentUser).toHaveBeenCalledExactlyOnceWith(saved);
    expect(panel.emailChallenge()).toBeNull();
  });

  it('keeps the challenge and the current user when confirming the email fails', async () => {
    account.confirmCurrentEmail.mockReturnValue(failure());
    const panel = fixture.componentInstance;
    panel.emailChallenge.set({ challengeId: 'challenge-1', email: initialUser.email });
    panel.emailCode.set('123456');

    panel.confirmEmailCode();
    await fixture.whenStable();

    expect(setCurrentUser).not.toHaveBeenCalled();
    expect(panel.emailChallenge()).toEqual({
      challengeId: 'challenge-1',
      email: initialUser.email,
    });
    expect(panel.confirmingCode()).toBe(false);
  });
  //#endif
});
