import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { toast } from '@spartan-ng/brain/sonner';
import { of, Subject, throwError } from 'rxjs';

import { TwoFactorChallenge } from './two-factor-challenge';
import { ApplicationHttpError } from '../../../../core/errors/application-http-error';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../../../core/i18n/transloco.testing';
//#endif
import { AccountService } from '../../services/account-service';

import type { MockedObject } from 'vitest';

function rejection(code: string, detail = code, status = 401): ApplicationHttpError {
  return ApplicationHttpError.from(
    new HttpErrorResponse({ status, error: { status, code, title: code, detail } }),
  );
}

describe('TwoFactorChallenge', () => {
  let fixture: ComponentFixture<TwoFactorChallenge>;
  let account: Pick<MockedObject<AccountService>, 'completeTwoFactorLogin'>;
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
    fixture = TestBed.createComponent(TwoFactorChallenge);
    fixture.componentRef.setInput('token', 'challenge-token');
    completed = 0;
    cancelled = 0;
    fixture.componentInstance.completed.subscribe(() => completed++);
    fixture.componentInstance.cancelled.subscribe(() => cancelled++);
    fixture.detectChanges();
  });

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

  function shownError(): HTMLElement | null {
    return (fixture.nativeElement as HTMLElement).querySelector<HTMLElement>(
      '[data-testid="two-factor-error"]',
    );
  }

  // 码不对：原因就地显示在输入框下方并关联到真实输入框，不弹 toast；清空后焦点回到输入框，
  // 说明留到本人开始重输
  it('shows a rejected code in place, clears it and keeps the reason until typing again', async () => {
    const notify = vi.spyOn(toast, 'error').mockImplementation(() => '');
    account.completeTwoFactorLogin.mockReturnValue(
      throwError(() => rejection('Auth:TwoFactorCodeInvalid', 'Wait for the next code.', 400)),
    );

    type('000000');
    await submit();
    await new Promise((resolve) => requestAnimationFrame(resolve));

    const input = (fixture.nativeElement as HTMLElement).querySelector<HTMLInputElement>(
      '#two-factor-code',
    )!;
    expect(shownError()?.textContent?.trim()).toBe('Wait for the next code.');
    expect(shownError()?.getAttribute('role')).toBe('alert');
    expect(input.value).toBe('');
    expect(input.getAttribute('aria-invalid')).toBe('true');
    expect(input.getAttribute('aria-describedby')).toBe(shownError()!.id);
    expect(document.activeElement).toBe(input);
    expect(notify).not.toHaveBeenCalled();

    type('1');

    expect(shownError()).toBeNull();
    expect(input.hasAttribute('aria-invalid')).toBe(false);
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
    const toggle = Array.from(
      host.querySelectorAll<HTMLButtonElement>('button[variant="link"]'),
    )[0];

    toggle.click();
    await fixture.whenStable();
    await new Promise((resolve) => requestAnimationFrame(resolve));

    const input = host.querySelector<HTMLInputElement>('#two-factor-recovery-code')!;
    expect(host.querySelector('label[for="two-factor-recovery-code"]')).not.toBeNull();
    expect(document.activeElement).toBe(input);
  });
});
