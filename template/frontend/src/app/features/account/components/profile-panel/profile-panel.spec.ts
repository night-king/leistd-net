import { HttpErrorResponse } from '@angular/common/http';
import { signal, WritableSignal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { toast } from '@spartan-ng/brain/sonner';
import { of, throwError } from 'rxjs';

import { ProfilePanel } from './profile-panel';
import { ApplicationHttpError } from '../../../../core/errors/application-http-error';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../../../core/i18n/transloco.testing';
//#endif
import { AuthService } from '../../../../core/services/auth-service';
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
  };

  let fixture: ComponentFixture<ProfilePanel>;
  let currentUser: WritableSignal<unknown>;
  let setCurrentUser: Mock;
  let account: {
    updateCurrentUser: Mock;
    setAvatar: Mock;
    //#if (Email)
    getSecurityConfig: Mock;
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
      getSecurityConfig: vi
        .fn()
        .mockReturnValue(of({ enableEmailVerification: true, emailVerificationAvailable: true })),
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
        { provide: AuthService, useValue: { currentUser, setCurrentUser } },
        { provide: AccountService, useValue: account },
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

  // 换邮箱而会话不够新：弹出确认身份，带上当前口令重试同一次保存
  it('asks for the current password and retries the save with it when the session is not recent', async () => {
    const saved = { ...initialUser, email: 'alice@new.example.com' };
    account.updateCurrentUser
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
      .mockReturnValueOnce(of(saved));
    const panel = fixture.componentInstance;
    panel.profileForm.email().value.set('alice@new.example.com');

    panel.onSubmit();
    await fixture.whenStable();

    expect(panel.reauthVisible()).toBe(true);
    expect(setCurrentUser).not.toHaveBeenCalled();
    expect(toast.error).not.toHaveBeenCalled();

    panel.onSubmit('current-password');
    await fixture.whenStable();

    expect(account.updateCurrentUser).toHaveBeenLastCalledWith(
      expect.objectContaining({
        email: 'alice@new.example.com',
        currentPassword: 'current-password',
      }),
    );
    expect(setCurrentUser).toHaveBeenCalledExactlyOnceWith(saved);
    expect(panel.reauthVisible()).toBe(false);
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
