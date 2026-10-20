import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { HlmTooltipImports } from '@spartan-ng/helm/tooltip';
import { userEvent } from 'vitest/browser';

import { TruncatedTooltip } from './truncated-tooltip';
import { parkPointer } from '../utils/pointer.testing';

@Component({
  imports: [...HlmTooltipImports, TruncatedTooltip],
  template: `
    <button
      type="button"
      class="block"
      [style.width.px]="width()"
      [hlmTooltip]="label()"
      [appTruncatedTooltip]="text"
    >
      <span #text class="block truncate">{{ label() }}</span>
    </button>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
class Host {
  readonly width = signal(80);
  readonly label = signal('Account & security settings');
}

/** 键盘聚焦与悬停一样触发提示：截断时给出全文，放得下时不提示；宽度与文案变化后重新判断。 */
describe('TruncatedTooltip', () => {
  let fixture: ComponentFixture<Host>;

  const tooltip = () => document.querySelector<HTMLElement>('[role="tooltip"]');

  beforeEach(async () => {
    await createHost();
  });

  async function createHost(): Promise<void> {
    fixture = TestBed.createComponent(Host);
    await settle();
    // 物理指针会跨用例保留；就绪后移到空白处，避免先前悬停的提示干扰键盘断言。
    await parkPointer();
    await expect.poll(() => tooltip()).toBeNull();
  }

  async function settle(): Promise<void> {
    fixture.detectChanges();
    // ResizeObserver 与 MutationObserver 在下一帧前回调
    await new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve)));
    await fixture.whenStable();
  }

  /** 聚焦后有没有出现提示；失焦后提示收起。延时由假时钟推进到期，"没出现"即禁用。 */
  async function focusTooltip(): Promise<string | null> {
    const button = (fixture.nativeElement as HTMLElement).querySelector('button')!;
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval'] });
    let text: string | null;
    try {
      button.focus();
      await vi.advanceTimersByTimeAsync(0);
      await fixture.whenStable();
      text = tooltip()?.textContent?.trim() ?? null;
      button.blur();
      await vi.advanceTimersByTimeAsync(0);
    } finally {
      vi.useRealTimers();
    }
    await expect.poll(() => tooltip()).toBeNull();
    return text;
  }

  it('shows the full label on focus when it is cut', async () => {
    expect(await focusTooltip()).toBe('Account & security settings');
  });

  it('stays silent when the label fits', async () => {
    fixture.componentInstance.width.set(400);
    await settle();

    expect(await focusTooltip()).toBeNull();
  });

  it('clears a retained pointer before testing focus on a replacement host', async () => {
    const button = (fixture.nativeElement as HTMLElement).querySelector('button')!;
    await userEvent.hover(button);
    await expect.poll(() => tooltip()?.textContent?.trim()).toBe('Account & security settings');

    // 销毁宿主不会移动物理指针，新宿主在相同位置出现时仍可能被悬停。
    fixture.destroy();
    await createHost();
    fixture.componentInstance.width.set(400);
    await settle();

    expect(await focusTooltip()).toBeNull();
  });

  it('measures again when the text changes', async () => {
    fixture.componentInstance.width.set(200);
    fixture.componentInstance.label.set('Short');
    await settle();
    expect(await focusTooltip()).toBeNull();

    fixture.componentInstance.label.set('A much longer label that no longer fits the button');
    await settle();

    expect(await focusTooltip()).toBe('A much longer label that no longer fits the button');
  });
});
