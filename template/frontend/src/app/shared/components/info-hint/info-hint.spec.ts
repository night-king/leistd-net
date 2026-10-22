import { ChangeDetectionStrategy, Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { userEvent } from 'vitest/browser';

import { InfoHint } from './info-hint';
import { expectTouchTarget, setCoarsePointer } from '../../utils/coarse-pointer.testing';
import { parkPointer } from '../../utils/pointer.testing';

@Component({
  selector: 'app-test-info-hint-host',
  imports: [InfoHint],
  // 上下留白：命中检测只认视口内的点，按钮要能滚到视口中间
  template: `<div class="py-40">
      <h3 class="flex items-center gap-1">Email <app-info-hint text="Applies everywhere" /></h3>
    </div>
    <p data-testid="elsewhere">Elsewhere</p>`,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
class Host {}

// 与设置页面板标题同一结构：图标随宿主行的 items-center 与标题居中
@Component({
  selector: 'app-test-info-hint-title-row',
  imports: [InfoHint],
  template: `<div class="flex items-center gap-1">
    <h3 class="text-base leading-none font-medium">Email</h3>
    <app-info-hint text="Applies everywhere" />
  </div>`,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
class TitleRow {}

@Component({
  selector: 'app-test-info-hint-paragraph',
  imports: [InfoHint],
  template: `<p class="w-[40rem]">
    <span data-testid="before">Before</span><app-info-hint text="Hint" /><span data-testid="after"
      >After</span
    >
  </p>`,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
class Paragraph {}

/** 补充说明对鼠标与键盘可用：悬停或聚焦时紧贴图标显示，Escape 收起；触屏目标不小于 44px。 */
describe('InfoHint', () => {
  async function render(): Promise<HTMLButtonElement> {
    const fixture = TestBed.createComponent(Host);
    await fixture.whenStable();
    return (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>(
      '[data-testid="info-hint"]',
    )!;
  }

  const tooltip = () => document.querySelector('[role="tooltip"]');
  // 移到页面别处：不用 unhover，它把指针挪去的位置可能正好落在说明上
  const elsewhere = () => document.querySelector<HTMLElement>('[data-testid="elsewhere"]')!;

  afterEach(async () => {
    await setCoarsePointer(false);
    await userEvent.keyboard('{Escape}');
  });

  // 说明锚定在图标旁（放不下时 Tooltip 自行换边），不落到视口别处。量浮层面板：内容入场动画带位移
  it('shows the hint next to the icon on hover and hides it when the pointer leaves', async () => {
    const button = await render();

    await userEvent.hover(button);
    await vi.waitFor(() => expect(tooltip()?.textContent).toContain('Applies everywhere'));
    const icon = button.getBoundingClientRect();
    const hint = tooltip()!.closest('.cdk-overlay-pane')!.getBoundingClientRect();
    const gap = Math.max(
      hint.left - icon.right,
      icon.left - hint.right,
      hint.top - icon.bottom,
      icon.top - hint.bottom,
    );
    expect(gap).toBeGreaterThanOrEqual(0);
    expect(gap).toBeLessThanOrEqual(16);

    await userEvent.hover(elsewhere());
    await vi.waitFor(() => expect(tooltip()).toBeNull());
  });

  it('shows the hint on keyboard focus and hides it on Escape', async () => {
    // 提示须由焦点打开：先停车并确认没有悬停出的提示
    await parkPointer();
    const button = await render();
    expect(tooltip()).toBeNull();

    button.focus();
    await vi.waitFor(() => expect(tooltip()?.textContent).toContain('Applies everywhere'));

    await userEvent.keyboard('{Escape}');
    await vi.waitFor(() => expect(tooltip()).toBeNull());
  });

  it('keeps a 44px target on coarse pointers', async () => {
    await setCoarsePointer(true);
    const button = await render();

    expectTouchTarget(button);
  });

  it('centers the icon on the row it sits in', async () => {
    const fixture = TestBed.createComponent(TitleRow);
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    const center = (element: Element) => {
      const rect = element.getBoundingClientRect();
      return rect.top + rect.height / 2;
    };

    expect(center(root.querySelector('svg')!)).toBeCloseTo(center(root.querySelector('h3')!), 0);
  });

  // 嵌在文字里时只占图标宽度，前后文字留在同一行
  it('stays inline inside running text', async () => {
    const fixture = TestBed.createComponent(Paragraph);
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    const rect = (selector: string) => root.querySelector(selector)!.getBoundingClientRect();

    expect(rect('app-info-hint').width).toBeLessThanOrEqual(44);
    expect(rect('[data-testid="after"]').top).toBe(rect('[data-testid="before"]').top);
  });

  it('names the button with the hint for screen readers', async () => {
    const button = await render();

    expect(button.getAttribute('aria-label')).toBe('Applies everywhere');
  });
});
