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

  /** 真实点按：按钮因此获得焦点，关闭时焦点才有处可回。 */
  async function open(trigger: HTMLButtonElement): Promise<HTMLElement> {
    await userEvent.click(trigger);
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

  /** 进出场动画结束后再量尺寸、查焦点。 */
  async function settle(): Promise<void> {
    await fixture.whenStable();
    await Promise.all(document.getAnimations().map((animation) => animation.finished));
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

  it('wraps long names and details instead of cutting them off', async () => {
    const longLabel = 'Regional Compliance Reviewer for Cross-Border Settlement Operations';
    const longDetail = 'compliance.cross-border-settlement.regional-reviewer.read-write';
    const trigger = await render([{ id: '1', label: longLabel, detail: longDetail }]);
    await open(trigger);

    const [name, detail] = Array.from(list().querySelectorAll<HTMLElement>('li span span'));
    for (const text of [name, detail]) {
      expect(text.scrollWidth).toBeLessThanOrEqual(text.clientWidth);
      // 折成了不止一行
      expect(text.getBoundingClientRect().height).toBeGreaterThan(
        Number.parseFloat(getComputedStyle(text).lineHeight) * 1.5,
      );
    }
    expect(name.textContent).toBe(longLabel);
    expect(detail.textContent).toBe(longDetail);
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
      // 视觉高度保持徽章行的高度，命中区来自伪元素
      expect(trigger.getBoundingClientRect().height).toBeLessThan(44);

      expectTouchTarget(trigger);
      await tapAt(trigger, 0, -21);
      await expect.poll(() => panel()).not.toBeNull();
    });
  });
});
