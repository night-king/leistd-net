import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { toast } from '@spartan-ng/brain/sonner';

import { SecretRevealDialog } from './secret-reveal-dialog';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../../../../../core/i18n/transloco.testing';
//#endif

import type { Mock } from 'vitest';

@Component({
  imports: [SecretRevealDialog],
  template: `
    <app-secret-reveal-dialog
      [(visible)]="visible"
      [secret]="secret()"
      [application]="application()"
      kind="reset"
    />
  `,
})
class HostComponent {
  readonly visible = signal(false);
  readonly secret = signal('super-secret');
  readonly application = signal<{ clientId: string; displayName?: string } | null>({
    clientId: 'demo-client',
    displayName: 'Demo Portal',
  });
}

describe('SecretRevealDialog', () => {
  let writeText: Mock;
  let fixture: ComponentFixture<HostComponent>;
  let host: HostComponent;

  beforeEach(() => {
    writeText = vi.fn().mockName('writeText').mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', {
      value: { writeText },
      configurable: true,
    });

    TestBed.configureTestingModule({
      imports: [HostComponent],
      // prettier-ignore
      providers: [
                //#if (IncludeLocalization)
                ...provideTranslocoTesting(['en']),
                //#endif
            ],
    });
    fixture = TestBed.createComponent(HostComponent);
    host = fixture.componentInstance;
    fixture.detectChanges();
  });

  function dialog(): SecretRevealDialog {
    return fixture.debugElement.query(By.directive(SecretRevealDialog))
      .componentInstance as SecretRevealDialog;
  }

  const tooltip = () => document.querySelector<HTMLElement>('[role="tooltip"]');

  /**
   * 弹窗渲染在 document 上的浮层里。焦点由 CDK 在下一次渲染后设置，聚焦引出的提示再经 `timer(showDelay)`
   * （默认 0）才挂上浮层：焦点落定后排一个同为 0 的计时器，它按先后顺序在提示的计时器之后到期，
   * 此时若有提示就已经挂上，Esc 不会抢在提示之前按下。这道屏障能观察到提示由下面的对照用例证明。
   */
  async function afterFocusTooltipTimer(): Promise<void> {
    await new Promise((resolve) => setTimeout(resolve));
    await fixture.whenStable();
  }

  async function openDialog(): Promise<HTMLElement> {
    host.visible.set(true);
    await fixture.whenStable();
    await vi.waitFor(() => {
      const element = document.querySelector<HTMLElement>('[role="dialog"]');
      expect(element).not.toBeNull();
      expect(element!.contains(document.activeElement)).toBe(true);
    });
    await afterFocusTooltipTimer();
    return document.querySelector<HTMLElement>('[role="dialog"]')!;
  }

  function copyButtons(element: HTMLElement): HTMLButtonElement[] {
    return Array.from(element.querySelectorAll<HTMLButtonElement>('dd button'));
  }

  // 初始焦点若落在复制按钮上，按钮的提示会随焦点弹出并成为最上层浮层，第一次 Esc 只关掉提示。
  it('focuses the title instead of a copy button when it opens', async () => {
    const element = await openDialog();

    const title = element.querySelector('[data-slot="dialog-title"]');
    expect(document.activeElement).toBe(title);
    expect(copyButtons(element)).not.toContain(document.activeElement);
    expect(tooltip()).toBeNull();
  });

  // 对照：聚焦复制按钮会在 document 上挂出提示，且越过同一道计时器屏障时已经挂上；
  // 上面"没有提示"的断言与 Esc 用例因此不会空过。
  it('shows a copy button tooltip on the document by the time the focus barrier passes', async () => {
    const element = await openDialog();
    const [clientIdButton] = copyButtons(element);

    clientIdButton.focus();
    await afterFocusTooltipTimer();

    expect(tooltip()).not.toBeNull();
    await vi.waitFor(() =>
      expect(tooltip()?.textContent).toMatch(/Copy Client ID|openApp\.secret\.copyClientId/),
    );
  });

  it('closes on the first Escape press', async () => {
    await openDialog();

    document.activeElement!.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Escape', code: 'Escape', bubbles: true }),
    );

    await vi.waitFor(() => expect(host.visible()).toBe(false));
  });

  it('names the application the credentials belong to', async () => {
    const element = await openDialog();

    const describedBy = document.getElementById(element.getAttribute('aria-describedby')!);
    expect(describedBy?.textContent).toContain('Demo Portal');
    expect(element.textContent).toContain('demo-client');
    expect(element.textContent).toContain('super-secret');
  });

  it('falls back to the client id when the application has no display name', async () => {
    host.application.set({ clientId: 'demo-client' });
    const element = await openDialog();

    const describedBy = document.getElementById(element.getAttribute('aria-describedby')!);
    expect(describedBy?.textContent).toContain('demo-client');
  });

  it('copies the client id and the secret from their own buttons', async () => {
    vi.spyOn(toast, 'success').mockImplementation(() => '');
    const element = await openDialog();
    const [clientIdButton, secretButton] = copyButtons(element);

    clientIdButton.click();
    secretButton.click();

    expect(writeText.mock.calls).toEqual([['demo-client'], ['super-secret']]);
  });

  it('mirrors the dialog open/closed state into the visible model', () => {
    dialog().onStateChange('open');
    expect(dialog().visible()).toBe(true);

    dialog().onStateChange('closed');
    expect(dialog().visible()).toBe(false);
  });

  it('copies the secret to the clipboard and notifies on success', async () => {
    const successSpy = vi.spyOn(toast, 'success').mockImplementation(() => '');

    dialog().copy();

    expect(writeText).toHaveBeenCalledWith('super-secret');
    await new Promise((resolve) => setTimeout(resolve));
    expect(successSpy).toHaveBeenCalled();
  });

  it('does nothing when there is no secret to copy', async () => {
    host.secret.set('');
    await fixture.whenStable();

    dialog().copy();

    expect(writeText).not.toHaveBeenCalled();
  });
});
