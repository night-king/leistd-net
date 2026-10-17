import { ComponentFixture } from '@angular/core/testing';

/**
 * 确认身份对话框的测试装配：对话框经 `*hlmDialogPortal` 渲染在 document 上的 CDK 浮层里，
 * 不在调用方的 `fixture.nativeElement` 内，按 id 与 `data-testid` 从 document 上找。
 */
export function reauthenticateForm(): HTMLFormElement | null {
  return document.querySelector<HTMLFormElement>('[data-testid="reauthenticate-form"]');
}

/** 等对话框关闭并退场（关闭动画结束后才从浮层里移除）；超过约 2 秒仍在时返回 false。 */
export async function reauthenticateClosed(fixture: ComponentFixture<unknown>): Promise<boolean> {
  for (let frame = 0; frame < 120; frame++) {
    await fixture.whenStable();
    if (!reauthenticateForm()) {
      return true;
    }
    await new Promise((resolve) => requestAnimationFrame(resolve));
  }
  return false;
}

/** 对话框的"取消"按钮；没打开时为 null。 */
export function reauthenticateCancelButton(): HTMLButtonElement | null {
  return (
    reauthenticateForm()?.querySelector<HTMLButtonElement>(
      '[data-testid="reauthenticate-cancel"]',
    ) ?? null
  );
}

/** 对话框当前收哪种凭证：`password`、`code` 或 `signInAgain`；没打开时为 null。 */
export function reauthenticateMethod(): string | null {
  return reauthenticateForm()?.getAttribute('data-method') ?? null;
}

/** 对话框里就地显示的拒绝原因（可见的错误元素文字），没有时为空串。 */
export function reauthenticateErrorText(): string {
  const form = reauthenticateForm();
  if (!form) {
    return '';
  }
  return Array.from(form.querySelectorAll<HTMLElement>('hlm-field-error'))
    .filter((element) => !element.hidden)
    .map((element) => element.textContent?.trim() ?? '')
    .join(' ')
    .trim();
}

/** 验证码被拒后"刚用过的码要等新码"的提示文字，没有时为空串。 */
export function reauthenticateCodeReuseHint(): string {
  return (
    reauthenticateForm()
      ?.querySelector<HTMLElement>('[data-testid="reauthenticate-code-reuse-hint"]')
      ?.textContent?.trim() ?? ''
  );
}

/** 在对话框里输入口令并点"确认"。对话框须处于口令方式。 */
export async function confirmWithPassword(
  fixture: ComponentFixture<unknown>,
  password: string,
): Promise<void> {
  const input = document.getElementById('reauthenticate-password') as HTMLInputElement;
  input.value = password;
  input.dispatchEvent(new Event('input'));
  await fixture.whenStable();
  reauthenticateForm()!
    .querySelector<HTMLButtonElement>('[data-testid="reauthenticate-submit"]')!
    .click();
  await fixture.whenStable();
}

/**
 * 在对话框里输入身份验证器上的验证码；输满 6 位即自动提交。处于口令方式且可以切换时先切到验证码。
 */
export async function confirmWithCode(
  fixture: ComponentFixture<unknown>,
  code: string,
): Promise<void> {
  if (reauthenticateMethod() === 'password') {
    reauthenticateForm()!
      .querySelector<HTMLButtonElement>('[data-testid="reauthenticate-switch"]')!
      .click();
    await fixture.whenStable();
  }
  const input = document.getElementById('reauthenticate-code') as HTMLInputElement;
  input.value = code;
  input.dispatchEvent(new Event('input'));
  await fixture.whenStable();
}
