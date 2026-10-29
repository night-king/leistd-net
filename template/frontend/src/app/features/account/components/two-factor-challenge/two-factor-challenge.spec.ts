import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { toast } from '@spartan-ng/brain/sonner';
import { of, Subject, throwError } from 'rxjs';

import { TwoFactorChallenge, TwoFactorPrompt } from './two-factor-challenge';
import { ApplicationHttpError } from '../../../../core/errors/application-http-error';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../../../core/i18n/transloco.testing';
//#endif
import {
  fakeAssertion,
  REQUEST_OPTIONS,
  webAuthnError,
} from '../../../../core/webauthn/webauthn.testing';
import { AccountService } from '../../services/account-service';

import type { MockedObject } from 'vitest';

function rejection(code: string, detail = code, status = 401): ApplicationHttpError {
  return ApplicationHttpError.from(
    new HttpErrorResponse({ status, error: { status, code, title: code, detail } }),
  );
}

/** 只开了身份验证器：默认它，唯一的备选是恢复码。 */
const AUTHENTICATOR_ONLY: TwoFactorPrompt = {
  token: 'challenge-token',
  methods: ['authenticator', 'recoveryCode'],
  defaultMethod: 'authenticator',
  maskedEmail: null,
};

describe('TwoFactorChallenge', () => {
  let fixture: ComponentFixture<TwoFactorChallenge>;
  // prettier-ignore
  let account: Pick<
    MockedObject<AccountService>,
    | 'completeTwoFactorLogin'
    | 'beginTwoFactorPasskey'
    //#if (Email)
    | 'sendTwoFactorEmailCode'
    //#endif
  >;
  let completed: number;
  let cancelled: number;

  function type(value: string): void {
    const input = (fixture.nativeElement as HTMLElement).querySelector('input') as HTMLInputElement;
    input.value = value;
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  async function submit(): Promise<void> {
    (fixture.nativeElement as HTMLElement)
      .querySelector('form')!
      .dispatchEvent(new Event('submit'));
    await fixture.whenStable();
    fixture.detectChanges();
  }

  beforeEach(() => {
    account = {
      completeTwoFactorLogin: vi.fn().mockName('AccountService.completeTwoFactorLogin'),
      beginTwoFactorPasskey: vi
        .fn()
        .mockName('AccountService.beginTwoFactorPasskey')
        .mockReturnValue(of({ options: REQUEST_OPTIONS })),
      //#if (Email)
      sendTwoFactorEmailCode: vi
        .fn()
        .mockName('AccountService.sendTwoFactorEmailCode')
        .mockReturnValue(of({ expiresInSeconds: 300, retryAfterSeconds: 60 })),
      //#endif
    };
    TestBed.configureTestingModule({
      imports: [TwoFactorChallenge],
      // prettier-ignore
      providers: [
                { provide: AccountService, useValue: account },
                //#if (IncludeLocalization)
                ...provideTranslocoTesting(['en']),
                //#endif
            ],
    });
    render(AUTHENTICATOR_ONLY);
  });

  function render(prompt: TwoFactorPrompt): void {
    fixture?.destroy();
    fixture = TestBed.createComponent(TwoFactorChallenge);
    fixture.componentRef.setInput('prompt', prompt);
    completed = 0;
    cancelled = 0;
    fixture.componentInstance.completed.subscribe(() => completed++);
    fixture.componentInstance.cancelled.subscribe(() => cancelled++);
    fixture.detectChanges();
  }

  function byTestId(id: string): HTMLElement | null {
    return (fixture.nativeElement as HTMLElement).querySelector<HTMLElement>(
      `[data-testid="${id}"]`,
    );
  }

  it('submits the code and notifies the login page on success', async () => {
    account.completeTwoFactorLogin.mockReturnValue(of(undefined));

    type('123456');
    await submit();

    expect(account.completeTwoFactorLogin).toHaveBeenCalledTimes(1);

    expect(account.completeTwoFactorLogin).toHaveBeenCalledWith({
      token: 'challenge-token',
      code: '123456',
    });
    expect(completed).toBe(1);
  });

  // 验证器应用与短信常把验证码显示成"123 456"，整段粘贴进来也要能直接提交
  it('keeps only digits from a pasted separated code and submits once complete', async () => {
    account.completeTwoFactorLogin.mockReturnValue(of(undefined));

    const input = (fixture.nativeElement as HTMLElement).querySelector('input') as HTMLInputElement;
    const clipboardData = new DataTransfer();
    clipboardData.setData('text/plain', '123 456');
    input.dispatchEvent(new ClipboardEvent('paste', { clipboardData }));
    await fixture.whenStable();
    fixture.detectChanges();

    expect(account.completeTwoFactorLogin).toHaveBeenCalledTimes(1);

    expect(account.completeTwoFactorLogin).toHaveBeenCalledWith({
      token: 'challenge-token',
      code: '123456',
    });
    expect(completed).toBe(1);
  });

  it('stays on a wrong code and returns to the password step on expired credentials', async () => {
    account.completeTwoFactorLogin.mockReturnValue(
      throwError(() => rejection('Auth:TwoFactorCodeInvalid')),
    );
    type('000000');
    await submit();
    expect(cancelled).toBe(0);

    account.completeTwoFactorLogin.mockReturnValue(
      throwError(() => rejection('Auth:TwoFactorChallengeExpired')),
    );
    type('000000');
    await submit();
    expect(cancelled).toBe(1);
  });

  // 网络错误与服务端故障不作废这次第二步：就地说明，同一个码可以原样再交一次
  it('stays on a network or server failure and lets the same code be submitted again', async () => {
    account.completeTwoFactorLogin.mockReturnValue(
      throwError(() => rejection('Error:InternalServerError', 'Something went wrong.', 500)),
    );
    type('123456');
    await submit();

    expect(cancelled).toBe(0);
    expect(byTestId('two-factor-error')?.textContent).toContain('Something went wrong.');
    const failedCalls = account.completeTwoFactorLogin.mock.calls.length;

    account.completeTwoFactorLogin.mockReturnValue(of(undefined));
    await submit();

    expect(account.completeTwoFactorLogin).toHaveBeenCalledTimes(failedCalls + 1);
    expect(account.completeTwoFactorLogin).toHaveBeenLastCalledWith({
      token: 'challenge-token',
      code: '123456',
    });
    expect(completed).toBe(1);
  });

  // 发码状态与方式说明会就地变化：是读屏会播报的状态区
  it('announces the hint as a status region', () => {
    expect(byTestId('two-factor-hint')?.getAttribute('role')).toBe('status');
  });

  function shownError(): HTMLElement | null {
    return (fixture.nativeElement as HTMLElement).querySelector<HTMLElement>(
      '[data-testid="two-factor-error"]',
    );
  }

  function reuseHint(): HTMLElement | null {
    return (fixture.nativeElement as HTMLElement).querySelector<HTMLElement>(
      '[data-testid="two-factor-code-reuse-hint"]',
    );
  }

  // 码不对：原因就地显示在输入框下方并关联到真实输入框，不弹 toast；同一个码只能用一次，另提示刚用过的要等新码。
  // 清空后焦点回到输入框，说明留到本人开始重输
  it('shows a rejected code in place, clears it and keeps the reason until typing again', async () => {
    const notify = vi.spyOn(toast, 'error').mockImplementation(() => '');
    account.completeTwoFactorLogin.mockReturnValue(
      throwError(() =>
        rejection('Auth:TwoFactorCodeInvalid', 'The verification code is incorrect.', 400),
      ),
    );

    type('000000');
    await submit();
    await new Promise((resolve) => requestAnimationFrame(resolve));

    const input = (fixture.nativeElement as HTMLElement).querySelector<HTMLInputElement>(
      '#two-factor-code',
    )!;
    expect(shownError()?.textContent?.trim()).toBe('The verification code is incorrect.');
    expect(shownError()?.getAttribute('role')).toBe('alert');
    //#if (IncludeLocalization)
    expect(reuseHint()?.textContent?.trim()).toBe('common.twoFactorCodeReuseHint');
    //#else
    expect(reuseHint()?.textContent?.trim()).toBe(
      'If you just used this code, wait for a new one.',
    );
    //#endif
    expect(input.value).toBe('');
    expect(input.getAttribute('aria-invalid')).toBe('true');
    expect(input.getAttribute('aria-describedby')).toBe(`${shownError()!.id} ${reuseHint()!.id}`);
    expect(document.activeElement).toBe(input);
    expect(notify).not.toHaveBeenCalled();

    type('1');

    expect(shownError()).toBeNull();
    expect(reuseHint()).toBeNull();
    expect(input.hasAttribute('aria-invalid')).toBe(false);
  });

  // 恢复码各不相同、用过即作废，不提示等待新码
  it('does not suggest waiting for a new code when a recovery code is rejected', async () => {
    account.completeTwoFactorLogin.mockReturnValue(
      throwError(() =>
        rejection('Auth:TwoFactorCodeInvalid', 'The verification code is incorrect.', 400),
      ),
    );
    byTestId('two-factor-toggle-mode')!.click();
    await fixture.whenStable();

    type('ABCD-EFGH-1234');
    await submit();

    expect(shownError()?.textContent?.trim()).toBe('The verification code is incorrect.');
    expect(reuseHint()).toBeNull();
    // forceShow 的错误由 Field 统一挂到输入框的 aria-describedby，只出现一次
    const input = (fixture.nativeElement as HTMLElement).querySelector<HTMLInputElement>(
      '[data-testid="two-factor-recovery-input"]',
    )!;
    expect(input.getAttribute('aria-describedby')).toBe(shownError()!.id);
  });

  // 输满 6 位自动提交，随后的回车与请求在途时的再次提交都不再发
  it('submits a completed code only once', async () => {
    const response = new Subject<void>();
    account.completeTwoFactorLogin.mockReturnValue(response);

    type('123456');
    await submit();
    await submit();

    expect(account.completeTwoFactorLogin).toHaveBeenCalledTimes(1);
    response.next();
    response.complete();
  });

  // 改用恢复码：可见标签，切换后焦点落在恢复码输入框
  it('labels the recovery code input and focuses it after switching', async () => {
    const host = fixture.nativeElement as HTMLElement;
    byTestId('two-factor-toggle-mode')!.click();
    await fixture.whenStable();
    await new Promise((resolve) => requestAnimationFrame(resolve));

    const input = host.querySelector<HTMLInputElement>('#two-factor-recovery-code')!;
    expect(host.querySelector('label[for="two-factor-recovery-code"]')).not.toBeNull();
    expect(document.activeElement).toBe(input);
  });

  // 只有一种备选：直接写明换成什么，不折叠成"换一种验证方式"
  it('names the only alternative directly', () => {
    //#if (IncludeLocalization)
    expect(byTestId('two-factor-toggle-mode')?.textContent?.trim()).toBe(
      'account.twoFactorLogin.useRecovery',
    );
    //#else
    expect(byTestId('two-factor-toggle-mode')?.textContent?.trim()).toBe('Use a recovery code');
    //#endif
    expect(byTestId('two-factor-other-methods')).toBeNull();
  });

  // 主按钮下一行：左边返回、右边换方式，读屏与 Tab 顺序同视觉顺序
  it('puts going back and switching method on one row, back first', () => {
    const back = byTestId('two-factor-back')!;
    const toggle = byTestId('two-factor-toggle-mode')!;

    expect(back.parentElement).toBe(toggle.parentElement);
    expect(back.compareDocumentPosition(toggle) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  // 返回的文字左缘与主按钮可见左缘对齐：前置图标自带留白会让整行看起来缩进。
  it('aligns going back with the left edge of the submit button', () => {
    const back = byTestId('two-factor-back')!;
    const submit = byTestId('two-factor-submit')!;
    const text = document.createRange();
    text.selectNodeContents(back);
    const submitBorder = parseFloat(getComputedStyle(submit).borderLeftWidth);

    expect(back.querySelector('ng-icon, svg')).toBeNull();
    expect(
      Math.abs(
        text.getBoundingClientRect().left - (submit.getBoundingClientRect().left + submitBorder),
      ),
    ).toBeLessThan(1);
  });
  //#if (Email)

  describe('with email verification codes', () => {
    const BOTH: TwoFactorPrompt = {
      token: 'challenge-token',
      methods: ['authenticator', 'email', 'recoveryCode'],
      defaultMethod: 'authenticator',
      maskedEmail: 'ali***@example.com',
    };

    // 默认是身份验证器时不发信；两种以上备选收成一行，展开后按系统顺序列出，恢复码在最后
    it('folds two or more alternatives into one entry and sends no email until chosen', async () => {
      render(BOTH);

      expect(account.sendTwoFactorEmailCode).not.toHaveBeenCalled();
      expect(byTestId('two-factor-toggle-mode')).toBeNull();
      byTestId('two-factor-other-methods')!.click();
      fixture.detectChanges();

      const listed = Array.from(
        byTestId('two-factor-methods')!.querySelectorAll<HTMLElement>(
          '[data-testid^="two-factor-method-"]',
        ),
      ).map((button) => button.dataset['testid']);
      expect(listed).toEqual(['two-factor-method-email', 'two-factor-method-recoveryCode']);

      byTestId('two-factor-method-email')!.click();
      await fixture.whenStable();
      fixture.detectChanges();

      expect(account.sendTwoFactorEmailCode).toHaveBeenCalledExactlyOnceWith('challenge-token');
      expect(byTestId('two-factor-challenge')?.dataset['method']).toBe('email');
      expect(byTestId('two-factor-methods')).toBeNull();
    });

    // 展开后按钮换成列表：焦点落到列表第一项，不掉到页面顶部
    it('moves focus to the first alternative after expanding the list', async () => {
      render(BOTH);
      byTestId('two-factor-other-methods')!.click();
      fixture.detectChanges();
      await new Promise((resolve) => requestAnimationFrame(resolve));

      expect(document.activeElement).toBe(byTestId('two-factor-method-email'));
    });

    // 邮箱码已失效不是输错：就地说明，不回到口令步，可以重新获取
    it('keeps an expired email code in place instead of leaving the second step', async () => {
      account.completeTwoFactorLogin.mockReturnValue(
        throwError(() =>
          rejection('Auth:EmailCodeExpired', 'The email verification code has expired.', 400),
        ),
      );
      render({ ...BOTH, defaultMethod: 'email' });
      await fixture.whenStable();
      fixture.detectChanges();

      type('123456');
      await submit();

      expect(cancelled).toBe(0);
      expect(byTestId('two-factor-error')?.textContent).toContain('has expired');
    });

    // 首选是邮箱：进来即发码，不用再点；提交的是邮箱验证码
    it('sends the email code on arrival when email is the default and submits it', async () => {
      account.completeTwoFactorLogin.mockReturnValue(of(undefined));
      render({ ...BOTH, defaultMethod: 'email' });
      await fixture.whenStable();
      fixture.detectChanges();

      expect(account.sendTwoFactorEmailCode).toHaveBeenCalledExactlyOnceWith('challenge-token');
      //#if (IncludeLocalization)
      const hint = 'account.twoFactorLogin.emailHint';
      //#else
      const hint = 'ali***@example.com';
      //#endif
      expect(byTestId('two-factor-hint')?.textContent).toContain(hint);
      expect((byTestId('two-factor-email-resend') as HTMLButtonElement).disabled).toBe(true);

      type('654321');
      await submit();

      expect(account.completeTwoFactorLogin).toHaveBeenCalledWith({
        token: 'challenge-token',
        emailCode: '654321',
      });
      expect(completed).toBe(1);
    });

    // 发送中出现转圈时链接不左右移动：链接按钮没有左内边距，转圈不能再加一份。
    it('keeps the resend link in place while the code is being sent', async () => {
      account.sendTwoFactorEmailCode.mockReturnValue(new Subject());
      render({ ...BOTH, defaultMethod: 'email' });
      await fixture.whenStable();
      fixture.detectChanges();
      const resend = byTestId('two-factor-email-resend')!;

      expect(resend.querySelector('hlm-spinner')).not.toBeNull();
      expect(getComputedStyle(resend).paddingLeft).toBe('0px');
    });

    // 发得太频繁或方式不能用：就地说明，不退回密码那一步
    it('explains a refused send in place', async () => {
      account.sendTwoFactorEmailCode.mockReturnValue(
        throwError(() =>
          rejection('Auth:TwoFactorMethodUnavailable', 'Not available right now.', 400),
        ),
      );
      render({ ...BOTH, defaultMethod: 'email' });
      await fixture.whenStable();
      fixture.detectChanges();

      expect(shownError()?.textContent?.trim()).toBe('Not available right now.');
      expect(cancelled).toBe(0);
    });

    // 发信失败不作废这次第二步：就地说明，不再说"正在发送"，仍可换成身份验证器完成登录
    it('keeps the second step when the email cannot be sent and lets the person switch', async () => {
      account.sendTwoFactorEmailCode.mockReturnValue(
        throwError(() => rejection('Smtp:Unavailable', 'The email could not be sent.', 500)),
      );
      account.completeTwoFactorLogin.mockReturnValue(of(undefined));
      render({ ...BOTH, defaultMethod: 'email' });
      await fixture.whenStable();
      fixture.detectChanges();

      expect(cancelled).toBe(0);
      expect(shownError()?.textContent?.trim()).toBe('The email could not be sent.');
      //#if (IncludeLocalization)
      expect(byTestId('two-factor-hint')?.textContent).toContain(
        'account.twoFactorLogin.emailPending',
      );
      //#else
      expect(byTestId('two-factor-hint')?.textContent).toContain('A code will be sent to');
      //#endif
      expect((byTestId('two-factor-email-resend') as HTMLButtonElement).disabled).toBe(false);

      byTestId('two-factor-other-methods')!.click();
      fixture.detectChanges();
      byTestId('two-factor-method-authenticator')!.click();
      await fixture.whenStable();
      fixture.detectChanges();
      type('123456');
      await submit();

      expect(account.completeTwoFactorLogin).toHaveBeenCalledWith({
        token: 'challenge-token',
        code: '123456',
      });
      expect(completed).toBe(1);
    });

    // 这次第二步已经失效（过期、锁定）时才回到密码那一步
    it('returns to the password step when sending finds the attempt expired', async () => {
      vi.spyOn(toast, 'error').mockImplementation(() => '');
      account.sendTwoFactorEmailCode.mockReturnValue(
        throwError(() => rejection('Auth:TwoFactorChallengeExpired')),
      );
      render({ ...BOTH, defaultMethod: 'email' });
      await fixture.whenStable();

      expect(cancelled).toBe(1);
    });

    // 邮箱验证码被拒不提示"刚用过的码要等新码"：那只适用于身份验证器
    it('does not suggest waiting for a new code when an email code is rejected', async () => {
      account.completeTwoFactorLogin.mockReturnValue(
        throwError(() =>
          rejection('Auth:TwoFactorCodeInvalid', 'The verification code is incorrect.', 400),
        ),
      );
      render({ ...BOTH, defaultMethod: 'email' });
      await fixture.whenStable();

      type('000000');
      await submit();

      expect(shownError()?.textContent?.trim()).toBe('The verification code is incorrect.');
      expect(reuseHint()).toBeNull();
    });
  });
  //#endif

  describe('passkey', () => {
    /** 有通行密钥：默认它，备选是身份验证器与恢复码。 */
    const PASSKEY_DEFAULT: TwoFactorPrompt = {
      token: 'challenge-token',
      methods: ['passkey', 'authenticator', 'recoveryCode'],
      defaultMethod: 'passkey',
      maskedEmail: null,
    };

    afterEach(() => vi.restoreAllMocks());

    async function clickPasskey(): Promise<void> {
      byTestId('two-factor-passkey-submit')!.click();
      await fixture.whenStable();
      fixture.detectChanges();
    }

    it('shows a single button and starts the browser only after a click', async () => {
      const get = vi.spyOn(navigator.credentials, 'get').mockResolvedValue(fakeAssertion('cred-1'));
      account.completeTwoFactorLogin.mockReturnValue(of(undefined));
      render(PASSKEY_DEFAULT);
      await fixture.whenStable();

      expect(byTestId('two-factor-passkey-submit')).not.toBeNull();
      expect((fixture.nativeElement as HTMLElement).querySelector('form')).toBeNull();
      expect(get).not.toHaveBeenCalled();
      expect(account.beginTwoFactorPasskey).not.toHaveBeenCalled();

      await clickPasskey();

      expect(account.beginTwoFactorPasskey).toHaveBeenCalledExactlyOnceWith('challenge-token');
      expect(get).toHaveBeenCalledTimes(1);
      expect(account.completeTwoFactorLogin).toHaveBeenCalledExactlyOnceWith({
        token: 'challenge-token',
        passkey: expect.objectContaining({ id: 'cred-1', type: 'public-key' }),
      });
      expect(completed).toBe(1);
    });

    it('explains a rejected passkey in place and lets the user try again', async () => {
      vi.spyOn(navigator.credentials, 'get').mockResolvedValue(fakeAssertion());
      account.completeTwoFactorLogin.mockReturnValue(
        throwError(() =>
          rejection('Auth:PasskeyVerificationFailed', "The passkey couldn't be verified.", 400),
        ),
      );
      render(PASSKEY_DEFAULT);

      await clickPasskey();

      expect(byTestId('two-factor-error')!.textContent).toContain(
        "The passkey couldn't be verified.",
      );
      expect(cancelled).toBe(0);
      expect((byTestId('two-factor-passkey-submit') as HTMLButtonElement).disabled).toBe(false);

      account.completeTwoFactorLogin.mockReturnValue(of(undefined));
      await clickPasskey();

      // 每次点击都重新取一份断言选项
      expect(account.beginTwoFactorPasskey).toHaveBeenCalledTimes(2);
      expect(completed).toBe(1);
    });

    it('treats a cancelled browser prompt as a neutral note without the browser text', async () => {
      vi.spyOn(navigator.credentials, 'get').mockRejectedValue(webAuthnError('NotAllowedError'));
      render(PASSKEY_DEFAULT);

      await clickPasskey();

      expect(byTestId('two-factor-passkey-cancelled')).not.toBeNull();
      expect(byTestId('two-factor-error')).toBeNull();
      expect((fixture.nativeElement as HTMLElement).textContent).not.toContain('browser text');
      expect(account.completeTwoFactorLogin).not.toHaveBeenCalled();
    });

    it('returns to the password step when the sign-in attempt has expired', async () => {
      account.beginTwoFactorPasskey.mockReturnValue(
        throwError(() => rejection('Auth:TwoFactorChallengeExpired')),
      );
      vi.spyOn(toast, 'error').mockReturnValue('toast');
      render(PASSKEY_DEFAULT);

      await clickPasskey();

      expect(cancelled).toBe(1);
    });

    it('lists the other methods and switches to the authenticator code', async () => {
      render(PASSKEY_DEFAULT);

      byTestId('two-factor-other-methods')!.click();
      fixture.detectChanges();
      byTestId('two-factor-method-authenticator')!.click();
      fixture.detectChanges();

      expect(byTestId('two-factor-passkey')).toBeNull();
      expect(byTestId('two-factor-challenge')!.getAttribute('data-method')).toBe('authenticator');
    });

    it('offers the passkey as the only alternative with its own wording', () => {
      render({
        token: 'challenge-token',
        methods: ['passkey', 'authenticator'],
        defaultMethod: 'authenticator',
        maskedEmail: null,
      });

      const toggle = byTestId('two-factor-toggle-mode')!;
      expect(toggle.getAttribute('data-method')).toBe('passkey');
      toggle.click();
      fixture.detectChanges();

      expect(byTestId('two-factor-passkey-submit')).not.toBeNull();
    });
  });
});
