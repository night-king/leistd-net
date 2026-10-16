import { HttpErrorResponse } from '@angular/common/http';
import { Injector, runInInjectionContext, signal, WritableSignal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { of } from 'rxjs';

import { injectReauthentication, Reauthentication } from './reauthentication';
import { ApplicationHttpError } from '../../errors/application-http-error';
import { AuthService } from '../../services/auth-service';

import type { Mock } from 'vitest';

interface AccountState {
  hasPassword?: boolean;
  isTwoFactorEnabled?: boolean;
}

/**
 * 调用方的确认身份约定：服务端要求时才打开对话框并记下重试；带着凭证仍被拒时原因留在对话框里，
 * 方式不具备时刷新当前用户；与凭证无关的失败交还调用方。
 */
describe('injectReauthentication', () => {
  let reauth: Reauthentication;
  let currentUser: WritableSignal<AccountState | null>;
  let loadUser: Mock;
  let signInAgain: Mock;

  function failure(code: string, status = 400, detail = code): ApplicationHttpError {
    return ApplicationHttpError.from(new HttpErrorResponse({ status, error: { code, detail } }));
  }

  beforeEach(() => {
    currentUser = signal<AccountState | null>({
      hasPassword: true,
      isTwoFactorEnabled: true,
    });
    loadUser = vi.fn(() => of(undefined));
    signInAgain = vi.fn();
    TestBed.configureTestingModule({
      providers: [
        { provide: AuthService, useValue: { currentUser, loadUser, signInAgain } },
        { provide: Router, useValue: { url: '/workspace/settings/profile' } },
      ],
    });
    reauth = runInInjectionContext(TestBed.inject(Injector), () => injectReauthentication());
  });

  it('opens the dialog when required and retries with the proof handed over', () => {
    const retry = vi.fn();

    expect(reauth.handleError(failure('Auth:ReauthenticationRequired'), undefined, retry)).toBe(
      true,
    );
    expect(reauth.visible()).toBe(true);

    reauth.confirm({ twoFactorCode: '123456' });

    expect(retry).toHaveBeenCalledExactlyOnceWith({ twoFactorCode: '123456' });
    expect(reauth.saving()).toBe(true);
    expect(loadUser).not.toHaveBeenCalled();
  });

  it('keeps a rejected proof inside the dialog with the reason from the server', () => {
    reauth.handleError(failure('Auth:ReauthenticationRequired'), undefined, vi.fn());
    reauth.confirm({ password: 'wrong' });

    const handled = reauth.handleError(
      failure('Security:CurrentPasswordIncorrect', 400, 'The current password is incorrect.'),
      { password: 'wrong' },
      vi.fn(),
    );

    expect(handled).toBe(true);
    expect(reauth.visible()).toBe(true);
    expect(reauth.saving()).toBe(false);
    expect(reauth.error()).toBe('The current password is incorrect.');
  });

  // 锁定不是"再输一次"：原样显示服务端给出的原因
  it('shows a lockout reason inside the dialog', () => {
    reauth.handleError(failure('Auth:ReauthenticationRequired'), undefined, vi.fn());

    reauth.handleError(
      failure('Auth:UserTemporarilyLockedOut', 401, 'Too many attempts. Try again in 5 minutes.'),
      { password: 'wrong' },
      vi.fn(),
    );

    expect(reauth.error()).toBe('Too many attempts. Try again in 5 minutes.');
    expect(reauth.codeRejected()).toBe(false);
    expect(reauth.visible()).toBe(true);
  });

  // 验证码不对（可能是刚用过的码）：对话框据此提示等待新码；下一次交出凭证时清掉
  it('marks a rejected verification code so the dialog can suggest waiting for a new one', () => {
    reauth.handleError(failure('Auth:ReauthenticationRequired'), undefined, vi.fn());
    reauth.confirm({ twoFactorCode: '123456' });

    reauth.handleError(
      failure('Auth:TwoFactorCodeInvalid', 400, 'The verification code is incorrect.'),
      { twoFactorCode: '123456' },
      vi.fn(),
    );

    expect(reauth.error()).toBe('The verification code is incorrect.');
    expect(reauth.codeRejected()).toBe(true);

    reauth.confirm({ twoFactorCode: '654321' });

    expect(reauth.codeRejected()).toBe(false);
  });

  it('refreshes the current user when the shown method is not available', () => {
    reauth.handleError(failure('Auth:ReauthenticationRequired'), undefined, vi.fn());

    reauth.handleError(
      failure('Auth:ReauthenticationMethodUnavailable'),
      { password: 'stale' },
      vi.fn(),
    );

    expect(loadUser).toHaveBeenCalledOnce();
    expect(reauth.visible()).toBe(true);
  });

  // 只有既无口令又没开两步验证才会要求重新登录；本地状态与之不符时以服务端为准
  it('refreshes the current user when sign-in-again does not match the local state', () => {
    reauth.handleError(failure('Auth:SignInAgainRequired'), undefined, vi.fn());

    expect(loadUser).toHaveBeenCalledOnce();
    expect(reauth.visible()).toBe(true);
  });

  it('hands failures unrelated to the proof back to the caller and closes the dialog', () => {
    reauth.handleError(failure('Auth:ReauthenticationRequired'), undefined, vi.fn());

    expect(reauth.handleError(failure('User:EmailTaken', 409), { password: 'ok' }, vi.fn())).toBe(
      false,
    );
    expect(reauth.visible()).toBe(false);
    expect(reauth.handleError(failure('User:EmailTaken', 409), undefined, vi.fn())).toBe(false);
  });

  it('drops the pending retry when the dialog is closed', () => {
    const retry = vi.fn();
    reauth.handleError(failure('Auth:ReauthenticationRequired'), undefined, retry);

    reauth.setVisible(false);
    reauth.confirm({ password: 'late' });

    expect(retry).not.toHaveBeenCalled();
  });

  it('signs in again back to the current page by default', () => {
    reauth.signInAgain();
    reauth.signInAgain('/workspace/settings/security');

    expect(signInAgain.mock.calls).toEqual([
      ['/workspace/settings/profile'],
      ['/workspace/settings/security'],
    ]);
  });

  it('reads the options from the current user, not from the target of the operation', () => {
    currentUser.set({ hasPassword: false, isTwoFactorEnabled: true });

    expect(reauth.hasPassword()).toBe(false);
    expect(reauth.twoFactorEnabled()).toBe(true);
  });
});
