import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { page, userEvent } from 'vitest/browser';

import { ReadonlyListItem, ReadonlyListPopover } from './readonly-list-popover';
import { expectTouchTarget, setCoarsePointer, tapAt } from '../../utils/coarse-pointer.testing';

@Component({
  imports: [ReadonlyListPopover],
  template: `
    <div [style.padding-top.px]="offsetTop()">
      <app-readonly-list-popover
        [items]="items()"
        [triggerLabel]="'View all roles of Alice'"
        [title]="'Roles'"
        [subtitle]="'Alice'"
        [filterLabel]="'Filter roles'"
        [emptyText]="'No matching roles'"
        [filterThreshold]="threshold()"
      >
        <span data-testid="summary">Admin</span>
      </app-readonly-list-popover>
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
class HostComponent {
  readonly items = signal<ReadonlyListItem[]>([]);
  readonly threshold = signal(7);
  readonly offsetTop = signal(0);
}

function roles(count: number): ReadonlyListItem[] {
  return Array.from({ length: count }, (_, index) => ({
    id: `${index + 1}`,
    label: `Role ${index + 1}`,
    detail: `role.${index + 1}`,
  }));
}

describe('ReadonlyListPopover', () => {
  let fixture: ComponentFixture<HostComponent>;

  beforeEach(() => {
    fixture = TestBed.createComponent(HostComponent);
    onTestFinished(() => document.querySelector('.cdk-overlay-container')?.remove());
  });

  async function render(items: ReadonlyListItem[]): Promise<HTMLButtonElement> {
    fixture.componentInstance.items.set(items);
    fixture.autoDetectChanges();
    await fixture.whenStable();
    return (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('button')!;
  }

  /** 规则用例经 DOM 点击进入处理器，并保留关闭时的焦点归还目标。 */
  async function open(trigger: HTMLButtonElement): Promise<HTMLElement> {
    trigger.focus();
    trigger.click();
    await expect.poll(() => panel()).not.toBeNull();
    await settle();
    return panel()!;
  }

  function panel(): HTMLElement | null {
    return document.querySelector<HTMLElement>('[data-testid="readonly-list-panel"]');
  }

  function list(): HTMLElement {
    return document.querySelector<HTMLElement>('[data-testid="readonly-list"]')!;
  }

  function filter(): HTMLInputElement | null {
    return document.querySelector<HTMLInputElement>('[data-testid="readonly-list-filter"]');
  }

  function labels(): string[] {
    return Array.from(list().querySelectorAll('li')).map((li) => li.textContent!.trim());
  }

  /** 等待动画与 ResizeObserver 测量完成，避免布局已更新而截断绑定尚未更新。 */
  async function settle(): Promise<void> {
    await fixture.whenStable();
    await Promise.all(document.getAnimations().map((animation) => animation.finished));
    await new Promise<void>((resolve) => {
      const observer = new ResizeObserver(() => {
        observer.disconnect();
        resolve();
      });
      observer.observe(list());
    });
    await fixture.whenStable();
  }

  it('wraps the projected summary in one button named by the trigger label', async () => {
    const trigger = await render(roles(2));

    expect(trigger.getAttribute('aria-label')).toBe('View all roles of Alice');
    expect(trigger.querySelector('[data-testid="summary"]')).not.toBeNull();
    // 标题是面板的输入，不能同时落成宿主的原生提示
    expect(trigger.closest('app-readonly-list-popover')!.hasAttribute('title')).toBe(false);
  });

  it('lists every item with the total count and names the dialog and the list by the title', async () => {
    const trigger = await render(roles(3));
    const content = await open(trigger);

    expect(labels()).toEqual(['Role 1role.1', 'Role 2role.2', 'Role 3role.3']);
    expect(content.textContent).toContain('Roles');
    expect(content.textContent).toContain('3');
    expect(content.textContent).toContain('Alice');
    expect(content.closest('.cdk-overlay-pane')!.getAttribute('aria-label')).toBe('Roles');
    expect(list().getAttribute('aria-label')).toBe('Roles');
  });

  it('focuses the list when there is no filter and returns focus to the trigger on Escape', async () => {
    const trigger = await render(roles(3));
    await open(trigger);

    expect(filter()).toBeNull();
    await expect.poll(() => document.activeElement).toBe(list());

    await userEvent.keyboard('{Escape}');
    await expect.poll(() => panel()).toBeNull();
    await expect.poll(() => document.activeElement).toBe(trigger);
  });

  it('focuses a labelled filter from the threshold on and returns focus on Escape', async () => {
    const trigger = await render(roles(7));
    await open(trigger);

    const input = filter()!;
    await expect.poll(() => document.activeElement).toBe(input);
    expect(document.querySelector(`label[for="${input.id}"]`)!.textContent).toBe('Filter roles');

    await userEvent.keyboard('{Escape}');
    await expect.poll(() => document.activeElement).toBe(trigger);
  });

  it('filters by label and detail ignoring case and says when nothing matches', async () => {
    const trigger = await render([
      { id: 'admins', label: 'Administrators', detail: 'admin' },
      { id: 'auditors', label: 'Auditors', detail: 'audit.readonly' },
      ...roles(6),
    ]);
    await open(trigger);

    await userEvent.fill(filter()!, 'ADMIN');
    await expect.poll(() => labels()).toEqual(['Administratorsadmin']);

    await userEvent.fill(filter()!, 'readonly');
    await expect.poll(() => labels()).toEqual(['Auditorsaudit.readonly']);

    await userEvent.fill(filter()!, 'nobody');
    await expect.poll(() => labels()).toEqual(['No matching roles']);
  });

  it('starts from every item again after closing and reopening', async () => {
    const trigger = await render(roles(8));
    await open(trigger);
    await userEvent.fill(filter()!, 'Role 8');
    await expect.poll(() => labels()).toHaveLength(1);

    await userEvent.keyboard('{Escape}');
    await expect.poll(() => panel()).toBeNull();
    await open(trigger);

    expect(filter()!.value).toBe('');
    expect(labels()).toHaveLength(8);
  });

  it('scrolls a long list within a height limit and reaches the last item by keyboard', async () => {
    const trigger = await render(roles(40));
    const content = await open(trigger);
    const scroller = list();
    expect(scroller.scrollHeight).toBeGreaterThan(scroller.clientHeight);
    // 高的视口里整个面板也只到 28rem，不铺满屏幕
    expect(content.getBoundingClientRect().height).toBeLessThanOrEqual(28 * 16 + 0.5);

    scroller.focus();
    await userEvent.keyboard('{End}');

    const last = Array.from(scroller.querySelectorAll('li')).at(-1)!;
    await expect
      .poll(
        () => last.getBoundingClientRect().bottom <= scroller.getBoundingClientRect().bottom + 0.5,
      )
      .toBe(true);
    // 滚到底后撤掉底部渐隐，最后一项不被遮淡
    await expect.poll(() => getComputedStyle(scroller).maskImage).toBe('none');
  });

  /** 一行里文字的中线：标题区与条目里的文字、等宽小字统一行高后垂直居中。 */
  function middle(element: Element): number {
    const box = element.getBoundingClientRect();
    return box.top + box.height / 2;
  }

  function row(id: string): HTMLElement {
    return list().querySelector<HTMLElement>(`[data-item-id="${id}"]`)!;
  }

  function cell(id: string, part: 'label' | 'detail'): HTMLElement | null {
    return row(id).querySelector<HTMLElement>(`[data-readonly-list-${part}]`);
  }

  function isTruncated(element: HTMLElement): boolean {
    return element.scrollWidth > element.clientWidth;
  }

  const tooltip = () => document.querySelector<HTMLElement>('[role="tooltip"]');

  /**
   * 规则矩阵用 DOM 指针事件进入提示处理器，保留真实布局与全文断言；窄屏用例另验真实指针。
   * 提示的显示与隐藏延时（默认 0）由假时钟推进，渲染帧与布局仍是真实的。
   */
  async function hoverTooltip(element: HTMLElement): Promise<string | null> {
    await settle();
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval'] });
    let text: string | null;
    try {
      element.dispatchEvent(new PointerEvent('pointerenter', { pointerType: 'mouse' }));
      await vi.advanceTimersByTimeAsync(0);
      await fixture.whenStable();
      text = tooltip()?.textContent?.trim() ?? null;
      element.dispatchEvent(new PointerEvent('pointerleave', { pointerType: 'mouse' }));
      await vi.advanceTimersByTimeAsync(0);
    } finally {
      vi.useRealTimers();
    }
    await expect.poll(() => tooltip()).toBeNull();
    return text;
  }

  it('keeps the header and each item on one vertically centred line', async () => {
    const trigger = await render(roles(3));
    const content = await open(trigger);

    const subtitle = content.querySelector('[data-testid="readonly-list-subtitle"]')!;
    const header = subtitle.closest('div')!;
    const title = Array.from(header.querySelectorAll('span')).find(
      (span) => span.textContent?.trim() === 'Roles',
    )!;
    const count = Array.from(header.querySelectorAll('span')).find(
      (span) => span.textContent?.trim() === '3',
    )!;
    for (const part of [subtitle, count]) {
      expect(Math.abs(middle(part) - middle(title))).toBeLessThan(1);
    }

    const label = cell('1', 'label')!;
    const detail = cell('1', 'detail')!;
    const bullet = row('1').querySelector('[aria-hidden="true"]')!;
    expect(Math.abs(middle(label) - middle(detail))).toBeLessThan(1);
    expect(Math.abs(middle(bullet) - middle(label))).toBeLessThan(1);
    // 圆点、名称在左，补充说明在右，两列之间至少留 24px
    expect(bullet.getBoundingClientRect().right).toBeLessThanOrEqual(
      label.getBoundingClientRect().left,
    );
    expect(
      detail.getBoundingClientRect().left - label.getBoundingClientRect().right,
    ).toBeGreaterThanOrEqual(24 - 0.5);
    // 单行：行高 20px 的文字只占一行
    expect(label.getBoundingClientRect().height).toBeLessThanOrEqual(20.5);
  });

  it('cuts long names and details to one line and shows the full text on hover only when cut', async () => {
    const longLabel = 'Regional Compliance Reviewer for Cross-Border Settlement Operations';
    const longDetail = 'compliance.cross-border-settlement.regional-reviewer.read-write';
    const trigger = await render([
      { id: 'both', label: longLabel, detail: longDetail },
      { id: 'name', label: longLabel },
      { id: 'detail', label: 'Ops', detail: longDetail },
      { id: 'short', label: 'Auditor', detail: 'audit' },
      { id: 'same', label: 'Member', detail: 'Member' },
    ]);
    await open(trigger);

    for (const element of [
      cell('both', 'label')!,
      cell('both', 'detail')!,
      cell('name', 'label')!,
    ]) {
      expect(getComputedStyle(element).textOverflow).toBe('ellipsis');
      expect(isTruncated(element)).toBe(true);
      expect(element.getBoundingClientRect().height).toBeLessThanOrEqual(20.5);
      // 不越出本行
      expect(element.getBoundingClientRect().right).toBeLessThanOrEqual(
        element.closest('li')!.getBoundingClientRect().right + 0.5,
      );
    }
    // 只有右列被截断：名称完整，补充说明省略
    expect(isTruncated(cell('detail', 'label')!)).toBe(false);
    expect(isTruncated(cell('detail', 'detail')!)).toBe(true);
    // 截断只是视觉的，文本仍是全文；与名称相同的补充说明不重复显示
    expect(cell('both', 'label')!.textContent).toBe(longLabel);
    expect(cell('both', 'detail')!.textContent).toBe(longDetail);
    expect(cell('same', 'detail')).toBeNull();

    expect(await hoverTooltip(row('both'))).toBe(`${longLabel} · ${longDetail}`);
    expect(await hoverTooltip(row('name'))).toBe(longLabel);
    expect(await hoverTooltip(row('detail'))).toBe(`Ops · ${longDetail}`);
    expect(await hoverTooltip(row('short'))).toBeNull();
    expect(await hoverTooltip(row('same'))).toBeNull();
  });

  // 面板打开期间视口变窄（分屏、旋转）：原本放得下的行被截断后要能看到全文，放宽后不再多余地弹提示
  it('re-measures cut items when the viewport narrows and widens while open', async () => {
    onTestFinished(() => page.viewport(1280, 800));
    const trigger = await render([{ id: 'edge', label: 'Settlement operations reviewer' }]);
    await open(trigger);
    expect(isTruncated(cell('edge', 'label')!)).toBe(false);
    expect(await hoverTooltip(row('edge'))).toBeNull();

    // 面板随视口收到 188px，文字区约 164px，放不下这个名称；宽屏时文字区约 288px，放得下
    await page.viewport(220, 800);
    await expect.poll(() => isTruncated(cell('edge', 'label')!)).toBe(true);
    expect(await hoverTooltip(row('edge'))).toBe('Settlement operations reviewer');

    await page.viewport(1280, 800);
    await expect.poll(() => isTruncated(cell('edge', 'label')!)).toBe(false);
    expect(await hoverTooltip(row('edge'))).toBeNull();
  });

  /** 悬停后提示整个落在视口内、文字没有被横向裁掉，全文可见。 */
  async function expectFullTextTooltipInViewport(target: HTMLElement, fullText: string) {
    await userEvent.hover(target);
    await expect.poll(() => tooltip()).not.toBeNull();
    const tip = tooltip()!;
    await Promise.all(tip.getAnimations({ subtree: true }).map((a) => a.finished));
    const box = tip.getBoundingClientRect();
    expect(box.left).toBeGreaterThanOrEqual(0);
    expect(box.right).toBeLessThanOrEqual(window.innerWidth);
    expect(tip.textContent?.trim()).toBe(fullText);
    const text = tip.querySelector<HTMLElement>('[data-readonly-list-full-text]')!;
    expect(text.scrollWidth).toBeLessThanOrEqual(text.clientWidth + 0.5);
    expect(tip.scrollWidth).toBeLessThanOrEqual(tip.clientWidth + 0.5);
    await userEvent.unhover(target);
    await expect.poll(() => tooltip()).toBeNull();
  }

  // 窄视口里的长标识：有断行机会的回调地址，以及完全连续、合法的角色名与稳定名称（没有任何自然断点），
  // 提示都整个落在视口内，文字折行而不越界
  it('keeps the full-text tooltip of long identifiers inside a narrow viewport', async () => {
    await page.viewport(390, 800);
    onTestFinished(() => page.viewport(1280, 800));
    const uri =
      'https://partner-portal.example.com/oauth/callback/settlement-operations/regional-reviewer?tenant=asia-pacific';
    const name = 'R'.repeat(64);
    const detail = 'S'.repeat(64);
    const trigger = await render([
      { id: 'uri', label: uri },
      { id: 'unbroken', label: name, detail },
    ]);
    await userEvent.click(trigger);
    await expect.poll(() => panel()).not.toBeNull();
    await settle();

    await expectFullTextTooltipInViewport(row('uri'), uri);
    await expectFullTextTooltipInViewport(row('unbroken'), `${name} · ${detail}`);
  });

  // 横屏手机：宽大于高，视口只有 390px 高，面板整体限高，标题、筛选框与最后一项都能看到
  it('keeps the whole panel inside a short landscape viewport', async () => {
    await page.viewport(844, 390);
    onTestFinished(() => page.viewport(1280, 800));
    fixture.componentInstance.offsetTop.set(120);
    const trigger = await render(roles(30));
    const content = await open(trigger);

    const box = content.getBoundingClientRect();
    expect(box.top).toBeGreaterThanOrEqual(0);
    expect(box.bottom).toBeLessThanOrEqual(window.innerHeight);
    const input = filter()!.getBoundingClientRect();
    expect(input.top).toBeGreaterThanOrEqual(box.top);
    expect(input.bottom).toBeLessThanOrEqual(box.bottom);

    const scroller = list();
    scroller.focus();
    await userEvent.keyboard('{End}');
    const last = Array.from(scroller.querySelectorAll('li')).at(-1)!;
    await expect
      .poll(() => last.getBoundingClientRect().bottom <= Math.min(box.bottom, window.innerHeight))
      .toBe(true);
  });

  describe('on coarse pointers', () => {
    afterEach(async () => {
      await setCoarsePointer(false);
    });

    it('gives the summary button a 44px target that opens the panel from its outer edge', async () => {
      await setCoarsePointer(true);
      fixture.componentInstance.offsetTop.set(40);
      const trigger = await render(roles(2));
      // 按钮本体就是 44px 的目标，不靠伸出盒子的伪元素：伸出的部分在矮行里会压到邻行
      expect(trigger.getBoundingClientRect().height).toBeGreaterThanOrEqual(44);

      expectTouchTarget(trigger);
      // 点按钮上沿内 1px：以中心为原点，向上半个高度
      await tapAt(trigger, 0, -(trigger.getBoundingClientRect().height / 2 - 1));
      await expect.poll(() => panel()).not.toBeNull();
    });
  });
});
