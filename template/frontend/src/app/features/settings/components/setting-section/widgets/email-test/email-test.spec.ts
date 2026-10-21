import { HttpErrorResponse } from '@angular/common/http';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { toast } from '@spartan-ng/brain/sonner';
import { Observable, of, Subject, throwError } from 'rxjs';

import { EmailTest } from './email-test';
import { ApplicationHttpError } from '../../../../../../core/errors/application-http-error';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../../../../../core/i18n/transloco.testing';
//#endif
import { AuthService } from '../../../../../../core/services/auth-service';
import { SettingService } from '../../../../../../core/settings/setting-service';

import type { Mock } from 'vitest';

/**
 * 测试发信：用当前生效的邮件参数发一封信。管理员靠失败原因改参数，
 * 所以失败时必须把服务端给出的原因原样显示，而不是一句"发送失败"。
 */
describe('EmailTest', () => {
  let fixture: ComponentFixture<EmailTest>;
  let sendTestEmail: Mock<(to: string) => Observable<void>>;

  function recipient(): HTMLInputElement {
    return (fixture.nativeElement as HTMLElement).querySelector<HTMLInputElement>(
      '[data-testid="email-test-to"]',
    )!;
  }

  function sendButton(): HTMLButtonElement {
    return (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>(
      '[data-testid="email-test-send"]',
    )!;
  }

  async function typeRecipient(value: string): Promise<void> {
    recipient().value = value;
    recipient().dispatchEvent(new Event('input'));
    await fixture.whenStable();
  }

  async function submit(): Promise<void> {
    (fixture.nativeElement as HTMLElement)
      .querySelector('form')!
      .dispatchEvent(new Event('submit', { cancelable: true }));
    await fixture.whenStable();
  }

  beforeEach(async () => {
    sendTestEmail = vi.fn().mockName('SettingService.sendTestEmail');
    sendTestEmail.mockReturnValue(of(undefined));
    vi.spyOn(toast, 'success').mockImplementation(() => '');
    vi.spyOn(toast, 'error').mockImplementation(() => '');

    TestBed.configureTestingModule({
      imports: [EmailTest],
      // prettier-ignore
      providers: [
        //#if (IncludeLocalization)
        ...provideTranslocoTesting(['en']),
        //#endif
        { provide: SettingService, useValue: { sendTestEmail } },
        {
          provide: AuthService,
          useValue: { currentUser: signal({ email: 'admin@example.test' }) },
        },
      ],
    });

    fixture = TestBed.createComponent(EmailTest);
    await fixture.whenStable();
  });

  afterEach(() => fixture.destroy());

  it('addresses the test email to the signed-in user by default', () => {
    expect(recipient().value).toBe('admin@example.test');
    // 没有可见标签，靠 aria-label 给输入框一个可读的名字
    expect(recipient().getAttribute('aria-label')).toBeTruthy();
    expect(sendButton().disabled).toBe(false);
  });

  it.each(['', 'admin', 'admin@example', 'a b@example.test'])(
    'does not send to %j',
    async (address) => {
      await typeRecipient(address);

      expect(sendButton().disabled).toBe(true);
      await submit();
      expect(sendTestEmail).not.toHaveBeenCalled();
    },
  );

  it('sends to the typed address and confirms', async () => {
    await typeRecipient('ops@example.test');

    sendButton().click();
    await fixture.whenStable();

    expect(sendTestEmail).toHaveBeenCalledExactlyOnceWith('ops@example.test');
    expect(toast.success).toHaveBeenCalledOnce();
    expect(toast.error).not.toHaveBeenCalled();
    expect(sendButton().disabled).toBe(false);
  });

  it('blocks a second send while the first is in flight', async () => {
    const response = new Subject<void>();
    sendTestEmail.mockReturnValue(response);

    sendButton().click();
    await fixture.whenStable();
    expect(sendButton().disabled).toBe(true);
    await submit();

    expect(sendTestEmail).toHaveBeenCalledOnce();

    response.next();
    response.complete();
    await fixture.whenStable();
    expect(sendButton().disabled).toBe(false);
  });

  it('shows the server reason and keeps the address when sending fails', async () => {
    const rejected = ApplicationHttpError.from(
      new HttpErrorResponse({
        status: 400,
        error: { detail: 'Test email failed: authentication rejected by the SMTP server.' },
      }),
    );
    sendTestEmail.mockReturnValue(throwError(() => rejected));
    await typeRecipient('ops@example.test');

    sendButton().click();
    await fixture.whenStable();

    expect(toast.error).toHaveBeenCalledOnce();
    expect(vi.mocked(toast.error).mock.calls[0][1]).toEqual({
      description: 'Test email failed: authentication rejected by the SMTP server.',
    });
    expect(toast.success).not.toHaveBeenCalled();
    expect(recipient().value).toBe('ops@example.test');
    expect(sendButton().disabled).toBe(false);
  });
});
