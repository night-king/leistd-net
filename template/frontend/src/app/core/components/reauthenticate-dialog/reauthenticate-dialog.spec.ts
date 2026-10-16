import { Component, provideZonelessChangeDetection, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { ReauthenticateDialog, ReauthenticationProof } from './reauthenticate-dialog';
import {
  confirmWithCode,
  confirmWithPassword,
  reauthenticateErrorText,
  reauthenticateForm,
  reauthenticateMethod,
} from './reauthenticate-dialog.testing';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../i18n/transloco.testing';
//#endif

/**
 * 确认是本人：按出示者的账号状态给出可选方式，只交出当前所选的那一种凭证；
 * 被拒的原因就地显示、对话框保持打开；没有可出示的凭证时只能重新登录。
 */
@Component({
  imports: [ReauthenticateDialog],
  template: `
    <app-reauthenticate-dialog
      [(visible)]="visible"
      [saving]="saving()"
      [(error)]="error"
      [hasPassword]="hasPassword()"
      [twoFactorEnabled]="twoFactorEnabled()"
      (confirmed)="confirmed.push($event)"
      (signInAgain)="signInAgainCount = signInAgainCount + 1"
    />
  `,
})
class HostComponent {
  readonly visible = signal(true);
  readonly saving = signal(false);
  readonly error = signal<string | null>(null);
  readonly hasPassword = signal(true);
  readonly twoFactorEnabled = signal(false);
  readonly confirmed: ReauthenticationProof[] = [];
  signInAgainCount = 0;
}

describe('ReauthenticateDialog', () => {
  let fixture: ComponentFixture<HostComponent>;
  let host: HostComponent;

  function passwordInput(): HTMLInputElement | null {
    return document.getElementById('reauthenticate-password') as HTMLInputElement | null;
  }

  function codeInput(): HTMLInputElement | null {
    return document.getElementById('reauthenticate-code') as HTMLInputElement | null;
  }

  function button(testId: string): HTMLButtonElement | null {
    return document.querySelector<HTMLButtonElement>(`[data-testid="${testId}"]`);
  }

  async function render(state: { hasPassword: boolean; twoFactorEnabled: boolean }) {
    host.hasPassword.set(state.hasPassword);
    host.twoFactorEnabled.set(state.twoFactorEnabled);
    await fixture.whenStable();
  }

  /** 等到渲染后回调（切换方式、被拒后的聚焦都在渲染完成后执行）。 */
  async function settle(): Promise<void> {
    await fixture.whenStable();
    await new Promise((resolve) => requestAnimationFrame(resolve));
    await fixture.whenStable();
  }

  async function typePassword(value: string): Promise<void> {
    passwordInput()!.value = value;
    passwordInput()!.dispatchEvent(new Event('input'));
    await fixture.whenStable();
  }

  beforeEach(async () => {
    TestBed.configureTestingModule({
      imports: [HostComponent],
      // prettier-ignore
      providers: [
        provideZonelessChangeDetection(),
        //#if (IncludeLocalization)
        ...provideTranslocoTesting(['en']),
        //#endif
      ],
    });

    fixture = TestBed.createComponent(HostComponent);
    host = fixture.componentInstance;
    await fixture.whenStable();
  });

  afterEach(() => fixture.destroy());

  describe('offers what the account can show', () => {
    it('asks only for the password when two-factor is off', async () => {
      await render({ hasPassword: true, twoFactorEnabled: false });

      expect(reauthenticateMethod()).toBe('password');
      expect(passwordInput()).not.toBeNull();
      expect(button('reauthenticate-switch')).toBeNull();
      expect(button('reauthenticate-sign-in-again-link')).toBeNull();
    });

    it('starts with the password and offers the authenticator as the alternative', async () => {
      await render({ hasPassword: true, twoFactorEnabled: true });

      expect(reauthenticateMethod()).toBe('password');
      expect(button('reauthenticate-switch')).not.toBeNull();
      expect(button('reauthenticate-sign-in-again-link')).toBeNull();
    });

    // 没有口令：只收验证码，另给"用不了身份验证器就重新登录"的后路（登录第二步可用恢复码）
    it('asks only for the code without a password and keeps signing in again reachable', async () => {
      await render({ hasPassword: false, twoFactorEnabled: true });

      expect(reauthenticateMethod()).toBe('code');
      expect(passwordInput()).toBeNull();
      expect(button('reauthenticate-switch')).toBeNull();
      expect(document.querySelector('label[for="reauthenticate-code"]')).not.toBeNull();

      button('reauthenticate-sign-in-again-link')!.click();
      await fixture.whenStable();

      expect(host.signInAgainCount).toBe(1);
      expect(host.confirmed).toEqual([]);
    });

    it('only offers signing in again when there is nothing to show', async () => {
      await render({ hasPassword: false, twoFactorEnabled: false });

      expect(reauthenticateMethod()).toBe('signInAgain');
      expect(passwordInput()).toBeNull();
      expect(codeInput()).toBeNull();
      expect(button('reauthenticate-submit')).toBeNull();

      button('reauthenticate-sign-in-again-submit')!.click();
      await fixture.whenStable();

      expect(host.signInAgainCount).toBe(1);
    });
  });

  describe('switching between password and code', () => {
    it('moves focus to the new input and clears what was typed in the other one', async () => {
      await render({ hasPassword: true, twoFactorEnabled: true });
      await typePassword('typed-password');

      button('reauthenticate-switch')!.click();
      await settle();

      expect(reauthenticateMethod()).toBe('code');
      expect(document.activeElement).toBe(codeInput());

      button('reauthenticate-switch')!.click();
      await settle();

      expect(reauthenticateMethod()).toBe('password');
      expect(document.activeElement).toBe(passwordInput());
      expect(passwordInput()!.value).toBe('');
    });

    it('hands over only the chosen proof', async () => {
      await render({ hasPassword: true, twoFactorEnabled: true });
      await typePassword('typed-password');

      await confirmWithCode(fixture, '123456');

      expect(host.confirmed).toEqual([{ twoFactorCode: '123456' }]);
      expect(host.confirmed[0]).not.toHaveProperty('password');
    });

    it('hands over the password alone', async () => {
      await render({ hasPassword: true, twoFactorEnabled: true });

      await confirmWithPassword(fixture, 'admin-password');

      expect(host.confirmed).toEqual([{ password: 'admin-password' }]);
      expect(host.confirmed[0]).not.toHaveProperty('twoFactorCode');
    });
  });

  it('does not hand over an empty password and reveals the error on submit', async () => {
    await render({ hasPassword: true, twoFactorEnabled: false });

    button('reauthenticate-submit')!.click();
    await fixture.whenStable();

    expect(host.confirmed).toEqual([]);
    expect(reauthenticateErrorText()).not.toBe('');
  });

  // 输满 6 位自动提交，与随后的回车提交只交一次；在途时不再交
  it('submits a completed code once and not again while saving', async () => {
    await render({ hasPassword: false, twoFactorEnabled: true });

    await confirmWithCode(fixture, '654321');
    reauthenticateForm()!.dispatchEvent(new Event('submit'));
    await fixture.whenStable();

    expect(host.confirmed).toEqual([{ twoFactorCode: '654321' }]);

    host.saving.set(true);
    await fixture.whenStable();
    await confirmWithCode(fixture, '111111');

    expect(host.confirmed).toHaveLength(1);
  });

  describe('a rejected proof', () => {
    it('stays open with the reason under the password, linked to the input, until it is edited', async () => {
      await render({ hasPassword: true, twoFactorEnabled: false });
      await confirmWithPassword(fixture, 'wrong-password');

      host.error.set('The current password is incorrect.');
      await settle();

      const input = passwordInput()!;
      expect(host.visible()).toBe(true);
      expect(reauthenticateErrorText()).toBe('The current password is incorrect.');
      expect(input.getAttribute('aria-invalid')).toBe('true');
      const describedBy = input.getAttribute('aria-describedby')?.split(' ') ?? [];
      const error = Array.from(reauthenticateForm()!.querySelectorAll('hlm-field-error')).find(
        (element) => !(element as HTMLElement).hidden,
      )!;
      expect(describedBy).toContain(error.id);
      expect(document.activeElement).toBe(input);

      await typePassword('wrong-password2');

      expect(host.error()).toBeNull();
      expect(reauthenticateErrorText()).toBe('');
    });

    // 验证码被拒后清空重输：说明不随清空消失，读屏能读到无效与原因，焦点回到输入框
    it('clears a rejected code but keeps the reason until a new code is typed', async () => {
      await render({ hasPassword: false, twoFactorEnabled: true });
      await confirmWithCode(fixture, '000000');

      host.error.set('Wait for the next code.');
      await settle();

      const input = codeInput()!;
      expect(input.value).toBe('');
      expect(reauthenticateErrorText()).toBe('Wait for the next code.');
      expect(input.getAttribute('aria-invalid')).toBe('true');
      expect(input.getAttribute('aria-describedby')?.split(' ')).toContain(
        'reauthenticate-code-error',
      );
      expect(document.activeElement).toBe(input);

      input.value = '1';
      input.dispatchEvent(new Event('input'));
      await fixture.whenStable();

      expect(host.error()).toBeNull();
      expect(input.hasAttribute('aria-invalid')).toBe(false);
    });

    // 同一个码再次被拒：清空后重新输入同一个码仍会再交一次
    it('accepts the same code again after it was rejected and cleared', async () => {
      await render({ hasPassword: false, twoFactorEnabled: true });
      await confirmWithCode(fixture, '000000');
      host.error.set('The verification code is incorrect.');
      await settle();

      await confirmWithCode(fixture, '000000');

      expect(host.confirmed).toEqual([{ twoFactorCode: '000000' }, { twoFactorCode: '000000' }]);
    });
  });

  // 账号状态刷新后可选方式变了（例如方式不具备）：按新状态重新给出输入，不再交已不存在的方式
  it('follows a changed account state and drops what was typed for the old method', async () => {
    await render({ hasPassword: true, twoFactorEnabled: true });
    await typePassword('typed-password');

    host.error.set('This verification method is not available for your account.');
    await render({ hasPassword: false, twoFactorEnabled: true });
    await settle();

    expect(reauthenticateMethod()).toBe('code');
    expect(reauthenticateErrorText()).toBe(
      'This verification method is not available for your account.',
    );
    await confirmWithCode(fixture, '123456');
    expect(host.confirmed).toEqual([{ twoFactorCode: '123456' }]);
  });

  it('clears the input and the reason when closed', async () => {
    await render({ hasPassword: true, twoFactorEnabled: true });
    await typePassword('typed-password');
    host.error.set('The current password is incorrect.');
    await fixture.whenStable();

    host.visible.set(false);
    await fixture.whenStable();
    host.visible.set(true);
    await fixture.whenStable();

    expect(host.error()).toBeNull();
    expect(reauthenticateMethod()).toBe('password');
    expect(passwordInput()!.value).toBe('');
  });
});
