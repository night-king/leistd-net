import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
//#if (IncludeLocalization)
import { provideTransloco, TRANSLOCO_LOADER } from '@jsverse/transloco';
//#endif
import { provideIcons } from '@ng-icons/core';
import { lucideInfo } from '@ng-icons/lucide';
//#if (IncludeLocalization)
import { of } from 'rxjs';
//#endif

import { NotificationItem } from './notification-item';
import { NotificationOutputDto } from '../../dtos/notification.dto';
import { expectTouchTarget, setCoarsePointer } from '../../utils/coarse-pointer.testing';

const LONG_TITLE =
  'Quarterly settlement reconciliation for the cross-border operations ledger has finished with warnings';
const LONG_CONTENT =
  'Three entries could not be matched against the bank statement and were moved to the review queue. '.repeat(
    4,
  );

const NOTIFICATION = {
  id: 'n-1',
  title: LONG_TITLE,
  content: LONG_CONTENT,
  type: 'System',
  isRead: false,
  creationTime: new Date().toISOString(),
} as NotificationOutputDto;

@Component({
  imports: [NotificationItem],
  providers: [provideIcons({ lucideInfo })],
  template: `
    <div class="w-[400px]">
      <app-notification-item
        icon="lucideInfo"
        [notification]="notification()"
        [expanded]="expanded()"
        [removable]="removable()"
        (expandedChange)="expanded.set($event)"
        (removed)="removed = removed + 1"
        (opened)="opened = opened + 1"
      >
        <time notificationTime>10-20 23:21</time>
      </app-notification-item>
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
class Host {
  readonly notification = signal(NOTIFICATION);
  readonly expanded = signal(false);
  readonly removable = signal(false);
  opened = 0;
  removed = 0;
}

/** 工作台列表与铃铛面板同时打开时，同一条通知渲染两份。 */
@Component({
  imports: [NotificationItem],
  providers: [provideIcons({ lucideInfo })],
  template: `
    @for (copy of [1, 2]; track copy) {
      <app-notification-item
        class="block w-[400px]"
        icon="lucideInfo"
        [notification]="notification"
      />
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
class TwoCopiesHost {
  readonly notification = NOTIFICATION;
}

/** 摘要里标题一行、正文两行；被截断的才给展开入口，折叠时 › 指向右、展开后转向下。 */
describe('NotificationItem', () => {
  let fixture: ComponentFixture<Host>;
  const host = () => fixture.nativeElement as HTMLElement;
  const expandButton = () =>
    host().querySelector<HTMLButtonElement>('[data-testid="notification-expand"]');
  const title = () => host().querySelector<HTMLElement>('span[data-clamp]')!;
  const content = () => host().querySelector<HTMLElement>('p[data-clamp]')!;
  const lines = (element: HTMLElement) =>
    Math.round(
      element.getBoundingClientRect().height / parseFloat(getComputedStyle(element).lineHeight),
    );

  beforeEach(async () => {
    //#if (IncludeLocalization)
    // shared 不依赖 core：词条为空、文案回落成键名
    TestBed.configureTestingModule({
      providers: [
        provideTransloco({
          config: {
            availableLangs: ['en'],
            defaultLang: 'en',
            missingHandler: { logMissingKey: false },
          },
        }),
        { provide: TRANSLOCO_LOADER, useValue: { getTranslation: () => of({}) } },
      ],
    });
    //#endif
    fixture = TestBed.createComponent(Host);
    await settle();
  });

  async function settle(target: ComponentFixture<unknown> = fixture): Promise<void> {
    target.detectChanges();
    // 截断由 ResizeObserver 与 MutationObserver 在下一帧前量出
    await new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve)));
    await target.whenStable();
    target.detectChanges();
  }

  it('clamps a long notification and expands it from the summary', async () => {
    expect(lines(title())).toBe(1);
    expect(lines(content())).toBe(2);
    const toggle = expandButton()!;
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    const controlled = host().querySelector(`#${toggle.getAttribute('aria-controls')}`);
    expect(controlled?.contains(content())).toBe(true);
    expect(toggle.querySelector('ng-icon')!.classList).not.toContain('rotate-90');

    toggle.click();
    await settle();

    expect(fixture.componentInstance.expanded()).toBe(true);
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect(toggle.querySelector('ng-icon')!.classList).toContain('rotate-90');
    for (const element of [title(), content()]) {
      expect(lines(element)).toBeGreaterThan(1);
      expect(element.scrollHeight).toBeLessThanOrEqual(element.clientHeight);
    }
    expect(fixture.componentInstance.opened, 'expanding does not open the notification').toBe(0);

    toggle.click();
    await settle();
    expect(lines(content())).toBe(2);
  });

  // 展开后截断解除；收起那一刻若入口先被移除再重建，焦点会落到 body
  async function expectCollapseKeepsFocus(): Promise<void> {
    const toggle = expandButton()!;
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    toggle.focus();
    toggle.click();
    fixture.detectChanges();

    expect(expandButton()).toBe(toggle);
    expect(document.activeElement).toBe(toggle);
    await settle();
    expect(expandButton()).toBe(toggle);
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
  }

  it('keeps focus on the same control when collapsing', async () => {
    expandButton()!.click();
    await settle();

    await expectCollapseKeepsFocus();
  });

  // 展开状态由使用方保存（铃铛面板关掉再打开会新建这一行），新建时还没量过截断
  it('keeps focus when collapsing a row created expanded', async () => {
    fixture.destroy();
    fixture = TestBed.createComponent(Host);
    fixture.componentInstance.expanded.set(true);
    await settle();

    await expectCollapseKeepsFocus();
  });

  it('keeps ids unique and ARIA references within the row when shown twice', async () => {
    const twoCopies = TestBed.createComponent(TwoCopiesHost);
    await settle(twoCopies);
    const root = twoCopies.nativeElement as HTMLElement;
    const ids = Array.from(root.querySelectorAll('[id]'), (element) => element.id);
    expect(new Set(ids).size).toBe(ids.length);

    const rows = root.querySelectorAll('app-notification-item');
    expect(rows).toHaveLength(2);
    for (const row of rows) {
      const references = Array.from(
        row.querySelectorAll('[aria-controls], [aria-describedby]'),
        (element) =>
          element.getAttribute('aria-controls') ?? element.getAttribute('aria-describedby')!,
      );
      expect(references).toHaveLength(2);
      for (const id of references) {
        expect(row.querySelector(`#${id}`), id).not.toBeNull();
      }
    }
  });

  it('offers no expand control when the summary fits', async () => {
    fixture.componentInstance.notification.update((n) => ({
      ...n,
      title: 'Done',
      content: 'Saved.',
    }));
    await settle();

    expect(expandButton()).toBeNull();
  });

  // 标题与正文所在的容器一旦定位（relative 等），就会盖住铺满整行的主按钮，点文字点不到这一行
  it('lets a tap on the title or content reach the row', () => {
    const open = host().querySelector('[data-testid="notification-open"]');
    for (const element of [title(), content()]) {
      const box = element.getBoundingClientRect();
      const hit = document.elementFromPoint(box.left + 4, box.top + box.height / 2);
      expect(hit, element.id).toBe(open);
    }
  });

  it('opens from anywhere on the row and names the row by its title', () => {
    const open = host().querySelector<HTMLButtonElement>('[data-testid="notification-open"]')!;
    expect(open.textContent?.trim()).toBe(LONG_TITLE);
    expect(open.getAttribute('aria-describedby')).toBe(content().id);

    open.click();

    expect(fixture.componentInstance.opened).toBe(1);
  });

  it('labels the expand control with visible text', () => {
    //#if (IncludeLocalization)
    expect(expandButton()!.textContent?.trim()).toBe('common.showFullText');
    //#else
    expect(expandButton()!.textContent?.trim()).toBe('Show full text');
    //#endif
  });

  it('offers removal only when the list allows it', async () => {
    const remove = () =>
      host().querySelector<HTMLButtonElement>('[data-testid="notification-remove"]');
    expect(remove()).toBeNull();

    fixture.componentInstance.removable.set(true);
    await settle();
    remove()!.click();

    expect(fixture.componentInstance.removed).toBe(1);
    expect(fixture.componentInstance.opened, 'removing does not open the notification').toBe(0);
  });

  it('gives expand and remove 44px targets on touch screens', async () => {
    await setCoarsePointer(true);
    onTestFinished(() => setCoarsePointer(false));
    fixture.componentInstance.removable.set(true);
    await settle();

    expectTouchTarget(expandButton()!);
    expectTouchTarget(host().querySelector<HTMLElement>('[data-testid="notification-remove"]')!);
  });

  it('marks only unread notifications', async () => {
    expect(host().querySelector('[data-testid="notification-unread"]')).not.toBeNull();

    fixture.componentInstance.notification.update((n) => ({ ...n, isRead: true }));
    await settle();

    expect(host().querySelector('[data-testid="notification-unread"]')).toBeNull();
  });

  // 图标在浅色圆底里居中，圆底与标题顶部对齐；未读圆点在标题右侧
  it('aligns the icon badge with the top of the title', () => {
    const icon = host().querySelector('ng-icon')!;
    const badge = icon.parentElement!;
    const svgBox = icon.querySelector('svg')!.getBoundingClientRect();
    const badgeBox = badge.getBoundingClientRect();
    const titleBox = title().getBoundingClientRect();
    const dotBox = host()
      .querySelector('[data-testid="notification-unread"]')!
      .getBoundingClientRect();

    expect(
      Math.abs(svgBox.left + svgBox.width / 2 - (badgeBox.left + badgeBox.width / 2)),
    ).toBeLessThan(1);
    expect(
      Math.abs(svgBox.top + svgBox.height / 2 - (badgeBox.top + badgeBox.height / 2)),
    ).toBeLessThan(1);
    expect(Math.abs(badgeBox.top - titleBox.top)).toBeLessThan(1);
    expect(dotBox.left).toBeGreaterThanOrEqual(titleBox.right);
  });
});
