//#if (ExternalLogin)
import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';

import { ExternalAuthCallback } from './external-auth-callback';
import {
  confirmWithCode,
  reauthenticateForm,
} from '../../../../core/components/reauthenticate-dialog/reauthenticate-dialog.testing';
import { ApplicationHttpError } from '../../../../core/errors/application-http-error';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../../../core/i18n/transloco.testing';
//#endif
import { AuthService } from '../../../../core/services/auth-service';
import { SessionContextService } from '../../../../core/services/session-context-service';
import { AccountService } from '../../services/account-service';

/** 外部登录回调的接线：会话上下文须在导航之前建立；业务拒绝展示服务端下发的原因。 */
describe('ExternalAuthCallback', () => {
  let fixture: ComponentFixture<ExternalAuthCallback>;
  let calls: string[];
  let accountService: {
    externalLoginCallback: ReturnType<typeof vi.fn>;
    linkExternalLogin: ReturnType<typeof vi.fn>;
  };
  let authService: {
    loadUser: ReturnType<typeof vi.fn>;
    signInAgain: ReturnType<typeof vi.fn>;
    currentUser: never;
  };

  beforeEach(async () => {
    calls = [];

    accountService = {
      externalLoginCallback: vi.fn().mockName('AccountService.externalLoginCallback'),
      linkExternalLogin: vi.fn().mockName('AccountService.linkExternalLogin'),
    };
    accountService.externalLoginCallback.mockReturnValue(of(undefined) as never);

    authService = {
      loadUser: vi.fn().mockName('AuthService.loadUser'),
      signInAgain: vi.fn().mockName('AuthService.signInAgain'),
      // 只经外部登录建立、已开两步验证的账号：确认身份只能出示验证码
      currentUser: signal({ hasPassword: false, isTwoFactorEnabled: true }) as never,
    };
    authService.loadUser.mockReturnValue(of(undefined) as never);

    const sessionContext = {
      establish: vi
        .fn()
        .mockName('establish')
        .mockImplementation(async () => {
          calls.push('establish');
        }),
    };

    await TestBed.configureTestingModule({
      imports: [ExternalAuthCallback],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        //#if (IncludeLocalization)
        ...provideTranslocoTesting(['en']),
        //#endif
        { provide: AccountService, useValue: accountService },
        { provide: AuthService, useValue: authService },
        { provide: SessionContextService, useValue: sessionContext },
        {
          provide: ActivatedRoute,
          useValue: {
            snapshot: {
              queryParamMap: convertToParamMap({ intent: 'login' }),
              paramMap: convertToParamMap({ provider: 'github' }),
            },
          },
        },
      ],
    }).compileComponents();

    vi.spyOn(TestBed.inject(Router), 'navigate').mockImplementation(async () => {
      calls.push('navigate');
      return true;
    });

    fixture = TestBed.createComponent(ExternalAuthCallback);
  });

  it('establishes the session before navigating', async () => {
    fixture.detectChanges();
    await new Promise((resolve) => setTimeout(resolve));

    expect(calls).toContain('navigate');
    expect(calls.indexOf('establish')).toBeGreaterThanOrEqual(0);
    expect(calls.indexOf('establish')).toBeLessThan(calls.indexOf('navigate'));
  });

  it('preserves the protected return address when a second step is required', async () => {
    accountService.externalLoginCallback.mockReturnValue(
      of({
        requiresTwoFactor: true,
        twoFactorToken: 'challenge',
        returnUrl: '/connect/authorize?state=original',
      }),
    );
    fixture.detectChanges();
    await new Promise((resolve) => setTimeout(resolve));
    expect(TestBed.inject(Router).navigate).toHaveBeenCalledWith(['/auth/login'], {
      state: { twoFactorToken: 'challenge', returnUrl: '/connect/authorize?state=original' },
    });
    expect(calls).not.toContain('establish');
  });

  it('uses the protected return address after establishing a session', async () => {
    accountService.externalLoginCallback.mockReturnValue(
      of({ returnUrl: '/workspace/settings/security' }),
    );
    const navigate = vi
      .spyOn(TestBed.inject(Router), 'navigateByUrl')
      .mockImplementation(async () => {
        calls.push('navigate-return');
        return true;
      });
    fixture.detectChanges();
    await new Promise((resolve) => setTimeout(resolve));
    expect(navigate).toHaveBeenCalledWith('/workspace/settings/security');
    expect(calls.indexOf('establish')).toBeLessThan(calls.indexOf('navigate-return'));
  });

  it('shows the server reason when the sign-in is rejected', async () => {
    vi.spyOn(console, 'error').mockReturnValue(undefined);
    const reason = 'An account with this email already exists.';
    accountService.externalLoginCallback.mockReturnValue(
      throwError(() =>
        ApplicationHttpError.from(
          new HttpErrorResponse({
            status: 409,
            error: { code: 'ExternalAuth:AccountExistsSignInToLink', detail: reason },
          }),
        ),
      ),
    );

    fixture.detectChanges();
    await new Promise((resolve) => setTimeout(resolve));
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain(reason);
    expect(calls).not.toContain('navigate');
  });

  it('shows a generic message when the server fails', async () => {
    vi.spyOn(console, 'error').mockReturnValue(undefined);
    const detail = 'Internal failure detail';
    accountService.externalLoginCallback.mockReturnValue(
      throwError(() =>
        ApplicationHttpError.from(new HttpErrorResponse({ status: 500, error: { detail } })),
      ),
    );

    fixture.detectChanges();
    await new Promise((resolve) => setTimeout(resolve));
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).not.toContain(detail);
  });

  // 提供商处取消或协议失败：后端带原因码回到这里，页面不得再调用完成端点，并要给出回去的路。
  function returnWith(params: Record<string, string>): void {
    (
      TestBed.inject(ActivatedRoute).snapshot as unknown as { queryParamMap: unknown }
    ).queryParamMap = convertToParamMap(params);
  }

  it('shows the cancellation without completing and offers a way back to sign in', async () => {
    returnWith({ intent: 'login', error: 'cancelled' });
    authService.loadUser.mockReturnValue(
      throwError(() => ApplicationHttpError.from(new HttpErrorResponse({ status: 401 }))),
    );

    fixture.detectChanges();
    await new Promise((resolve) => setTimeout(resolve));
    fixture.detectChanges();

    expect(accountService.externalLoginCallback).not.toHaveBeenCalled();
    expect(calls).not.toContain('navigate');
    const back = (fixture.nativeElement as HTMLElement).querySelector('a[href="/auth/login"]');
    expect(back).not.toBeNull();
  });

  it('returns a cancelled link to the security settings without completing it', async () => {
    returnWith({ intent: 'link', error: 'cancelled' });

    fixture.detectChanges();
    await new Promise((resolve) => setTimeout(resolve));

    expect(accountService.linkExternalLogin).not.toHaveBeenCalled();
    expect(TestBed.inject(Router).navigate).toHaveBeenCalledWith(['/workspace/settings/security']);
  });

  // 绑定时会话不够新：票据没被消费，留在回调页请本人确认身份，带上验证码重试同一次绑定
  it('asks to confirm identity and retries the link with the code', async () => {
    returnWith({ intent: 'link' });
    accountService.linkExternalLogin
      .mockReturnValueOnce(throwError(() => reauthenticationRequired()))
      .mockReturnValueOnce(of(undefined));
    const navigate = TestBed.inject(Router).navigate;

    fixture.detectChanges();
    await new Promise((resolve) => setTimeout(resolve));
    await fixture.whenStable();

    expect(accountService.linkExternalLogin).toHaveBeenCalledWith('github', {
      password: undefined,
      twoFactorCode: undefined,
    });
    expect(navigate).not.toHaveBeenCalled();

    await confirmWithCode(fixture, '123456');
    await new Promise((resolve) => setTimeout(resolve));

    expect(accountService.linkExternalLogin).toHaveBeenLastCalledWith('github', {
      password: undefined,
      twoFactorCode: '123456',
    });
    expect(navigate).toHaveBeenCalledWith(['/workspace/settings/security']);
  });

  // 用不了身份验证器：重新登录后回到账户与安全重新发起绑定，不回到这张票据的回调页
  it('signs in again back to the security settings, not to the callback', async () => {
    returnWith({ intent: 'link' });
    accountService.linkExternalLogin.mockReturnValueOnce(
      throwError(() => reauthenticationRequired()),
    );

    fixture.detectChanges();
    await new Promise((resolve) => setTimeout(resolve));
    await fixture.whenStable();
    reauthenticateForm()!
      .querySelector<HTMLButtonElement>('[data-testid="reauthenticate-sign-in-again-link"]')!
      .click();
    await fixture.whenStable();

    expect(authService.signInAgain).toHaveBeenCalledExactlyOnceWith('/workspace/settings/security');
    expect(accountService.linkExternalLogin).toHaveBeenCalledOnce();
  });

  function reauthenticationRequired(): ApplicationHttpError {
    return ApplicationHttpError.from(
      new HttpErrorResponse({ status: 400, error: { code: 'Auth:ReauthenticationRequired' } }),
    );
  }

  // 不出示口令、关掉对话框：放弃这次绑定，回到账户与安全
  it('returns to the security settings when the password prompt is dismissed', async () => {
    returnWith({ intent: 'link' });
    accountService.linkExternalLogin.mockReturnValueOnce(
      throwError(() =>
        ApplicationHttpError.from(
          new HttpErrorResponse({ status: 400, error: { code: 'Auth:ReauthenticationRequired' } }),
        ),
      ),
    );

    fixture.detectChanges();
    await new Promise((resolve) => setTimeout(resolve));
    (
      fixture.componentInstance as unknown as { onReauthVisibleChange(v: boolean): void }
    ).onReauthVisibleChange(false);
    await new Promise((resolve) => setTimeout(resolve));

    expect(accountService.linkExternalLogin).toHaveBeenCalledOnce();
    expect(TestBed.inject(Router).navigate).toHaveBeenCalledWith(['/workspace/settings/security']);
  });

  // 后退键重放旧回调：会话仍在时直接回到应用，而不是对已登录用户显示"登录失败"。
  it('enters the application when a replayed callback fails but the session is still valid', async () => {
    returnWith({ intent: 'login', error: 'failed' });

    fixture.detectChanges();
    await new Promise((resolve) => setTimeout(resolve));
    fixture.detectChanges();

    expect(accountService.externalLoginCallback).not.toHaveBeenCalled();
    expect(calls).toEqual(['establish', 'navigate']);
    expect(
      (fixture.nativeElement as HTMLElement).querySelector('a[href="/auth/login"]'),
    ).toBeNull();
  });
});
//#endif
