import { ChangeDetectionStrategy, Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { userEvent } from 'vitest/browser';

import { InfoHint } from './info-hint';
import { expectTouchTarget, setCoarsePointer, tapAt } from '../../utils/coarse-pointer.testing';

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

/** 补充说明对鼠标、键盘、触屏都可用：悬停展开、点按切换、Escape 收起，触屏目标不小于 44px。 */
describe('InfoHint', () => {
  async function render(): Promise<HTMLButtonElement> {
    const fixture = TestBed.createComponent(Host);
    await fixture.whenStable();
    return (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>(
      '[data-testid="info-hint"]',
    )!;
  }

  const content = () => document.querySelector('[data-testid="info-hint-content"]');
  // 移到页面别处：不用 unhover，它把指针挪去的位置可能正好落在说明上
  const elsewhere = () => document.querySelector<HTMLElement>('[data-testid="elsewhere"]')!;

  afterEach(async () => {
    await setCoarsePointer(false);
    await userEvent.keyboard('{Escape}');
  });

  it('opens on hover and closes when the pointer leaves', async () => {
    const button = await render();

    await userEvent.hover(button);
    await vi.waitFor(() => expect(content()?.textContent).toContain('Applies everywhere'));

    await userEvent.hover(elsewhere());
    await vi.waitFor(() => expect(content()).toBeNull());
  });

  // 指针从按钮移到说明上继续读，说明不能消失；离开两者后才收起
  it('stays open while the pointer moves onto the explanation', async () => {
    const button = await render();

    await userEvent.hover(button);
    await vi.waitFor(() => expect(content()).not.toBeNull());
    await userEvent.hover(content()!);
    await new Promise((resolve) => setTimeout(resolve, 300));
    expect(content()).not.toBeNull();

    await userEvent.hover(elsewhere());
    await vi.waitFor(() => expect(content()).toBeNull());
  });

  it('opens on a tap with a 44px target and closes on Escape, then opens again', async () => {
    await setCoarsePointer(true);
    const button = await render();
    expectTouchTarget(button);

    await tapAt(button, 0, 0);
    await vi.waitFor(() => expect(content()).not.toBeNull());

    await userEvent.keyboard('{Escape}');
    await vi.waitFor(() => expect(content()).toBeNull());

    await tapAt(button, 0, 0);
    await vi.waitFor(() => expect(content()).not.toBeNull());
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
