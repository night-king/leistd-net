import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { fitCount, WorkspaceNav } from './workspace-nav';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../../core/i18n/transloco.testing';
//#endif
import { MenuGroup, MenuItem, NavigationService } from '../../services/navigation-service';

describe('fitCount', () => {
  const ruler = { items: [100, 100, 100], more: 60, gap: 4 };

  it('keeps every item when they all fit', () => {
    expect(fitCount(308, ruler, 3)).toBe(3);
  });

  it('reserves room for the more button once something overflows', () => {
    // 两项 + 两个间距 + 更多 = 268，第三项再要 104
    expect(fitCount(307, ruler, 3)).toBe(2);
    expect(fitCount(267, ruler, 3)).toBe(1);
  });

  it('keeps every item until the widths are measured', () => {
    expect(fitCount(0, ruler, 3)).toBe(3);
    expect(fitCount(200, ruler, 4)).toBe(4);
  });
});

/** 宿主给导航限定宽度，模拟顶栏里品牌右侧的剩余空间（测试视口 1280 宽，导航按 md 以上平铺）。 */
@Component({
  selector: 'app-test-host',
  imports: [WorkspaceNav],
  template: `<div class="flex" [style.width.px]="width()"><app-workspace-nav /></div>`,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
class TestHost {
  readonly width = signal(1200);
}

const LONG_LABELS = [
  'My account',
  'Deposits & withdrawals',
  'Trading accounts',
  'Statements',
  'Rewards',
  'Support',
];

describe('WorkspaceNav overflow', () => {
  const items: MenuItem[] = LONG_LABELS.map((label, index) => ({
    label,
    icon: 'lucideGauge',
    route: `/workspace/item-${index}`,
  }));
  const groups = signal<MenuGroup[]>([{ label: 'Workspace', items }]);

  async function render(width: number) {
    TestBed.configureTestingModule({
      // prettier-ignore
      providers: [
        provideRouter([]),
        { provide: NavigationService, useValue: { menuGroups: groups, isItemActive: () => false } },
        //#if (IncludeLocalization)
        ...provideTranslocoTesting(),
        //#endif
      ],
    });
    const fixture = TestBed.createComponent(TestHost);
    fixture.componentInstance.width.set(width);
    await settle(fixture);
    return fixture;
  }

  async function settle(fixture: { whenStable: () => Promise<unknown> }) {
    // ResizeObserver 在下一帧回调，回调写入信号后再渲染一轮
    await new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve)));
    await fixture.whenStable();
  }

  function inlineLinks(root: HTMLElement): HTMLAnchorElement[] {
    return Array.from(root.querySelectorAll<HTMLAnchorElement>('nav a[hlmNavigationMenuLink]'));
  }

  it('shows every item on one line when there is room', async () => {
    const fixture = await render(1200);
    const root: HTMLElement = fixture.nativeElement;

    expect(inlineLinks(root)).toHaveLength(items.length);
    expect(root.querySelector('[data-testid="workspace-nav-more"]')).toBeNull();
  });

  it('moves trailing items into the more menu instead of wrapping', async () => {
    const fixture = await render(560);
    const root: HTMLElement = fixture.nativeElement;
    const links = inlineLinks(root);

    expect(links.length).toBeGreaterThan(0);
    expect(links.length).toBeLessThan(items.length);
    expect(links.map((link) => link.textContent?.trim())).toEqual(
      LONG_LABELS.slice(0, links.length),
    );
    const more = root.querySelector<HTMLElement>('[data-testid="workspace-nav-more"]');
    expect(more).not.toBeNull();
    // 不换行：每个平铺链接都只有一行高
    const lineHeights = links.map((link) => link.getBoundingClientRect().height);
    expect(new Set(lineHeights).size).toBe(1);
    // 平铺项加「更多」不超出导航区
    const nav = root.querySelector('nav[hlmNavigationMenu]')!.getBoundingClientRect();
    expect(more!.getBoundingClientRect().right).toBeLessThanOrEqual(nav.right + 0.5);
  });

  it('brings items back when the space grows', async () => {
    const fixture = await render(560);
    const narrow = inlineLinks(fixture.nativeElement).length;

    fixture.componentInstance.width.set(1200);
    await settle(fixture);

    expect(narrow).toBeLessThan(items.length);
    expect(inlineLinks(fixture.nativeElement)).toHaveLength(items.length);
  });

  it('opens the more menu from the keyboard and keeps focus in it', async () => {
    const fixture = await render(560);
    const more = fixture.nativeElement.querySelector(
      '[data-testid="workspace-nav-more"]',
    ) as HTMLElement;

    more.focus();
    // CDK 菜单按 keyCode 判断方向键，构造的事件里它是 0，这里补上
    const arrowDown = new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true });
    Object.defineProperty(arrowDown, 'keyCode', { value: 40 });
    more.dispatchEvent(arrowDown);
    await settle(fixture);

    // 下拉的首项拿到焦点；导航自己的方向键管理没有把焦点抢回平铺链接
    expect(document.activeElement?.closest('[hlmDropdownMenuItem]')).not.toBeNull();
    expect(document.activeElement?.textContent?.trim()).toBe(
      LONG_LABELS[inlineLinks(fixture.nativeElement).length],
    );
  });
});
