import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
//#if (IncludeLocalization)
import { TranslocoService } from '@jsverse/transloco';
//#endif
import { userEvent } from 'vitest/browser';

import { fitCount, WorkspaceNav } from './workspace-nav';
//#if (IncludeLocalization)
import enTexts from '../../../../../public/i18n/en.json';
import { lineCount, pseudoCatalog } from '../../../core/i18n/pseudo-locale.testing';
import { provideTranslocoTesting } from '../../../core/i18n/transloco.testing';
//#endif
import { expectTouchTarget, setCoarsePointer } from '../../../shared/utils/coarse-pointer.testing';
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

  /** 在「更多」上按下方向键：CDK 菜单按 keyCode 判断方向键，构造的事件里它是 0，这里补上。 */
  function pressArrowDown(more: HTMLElement): void {
    more.focus();
    const arrowDown = new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true });
    Object.defineProperty(arrowDown, 'keyCode', { value: 40 });
    more.dispatchEvent(arrowDown);
  }

  /** 等浮层开合动画结束：几何与焦点要在动画之后读。 */
  async function settleOverlay(fixture: Awaited<ReturnType<typeof render>>): Promise<void> {
    await settle(fixture);
    await Promise.all(document.getAnimations().map((animation) => animation.finished));
  }

  const menuItems = () =>
    Array.from(document.querySelectorAll<HTMLElement>('[hlmDropdownMenuItem]'));

  it('opens the more menu from the keyboard and keeps focus in it', async () => {
    const fixture = await render(560);
    const more = fixture.nativeElement.querySelector(
      '[data-testid="workspace-nav-more"]',
    ) as HTMLElement;

    pressArrowDown(more);
    await settleOverlay(fixture);

    // 打开的菜单首项拿到焦点，导航自己的方向键管理没有把焦点抢回平铺链接
    expect(menuItems().length).toBeGreaterThan(0);
    expect(document.activeElement).toBe(menuItems()[0]);
  });

  // 菜单弹在静止的光标下面时，浏览器也会派发 mouseenter；那不是悬停，键盘打开仍落在首项。
  // 先把真实鼠标停在第二项的位置再关掉菜单，重新打开时它正好弹在光标下。
  it('keeps keyboard focus on the first item when the menu opens under a resting pointer', async () => {
    const fixture = await render(560);
    const more = fixture.nativeElement.querySelector(
      '[data-testid="workspace-nav-more"]',
    ) as HTMLElement;
    pressArrowDown(more);
    await settleOverlay(fixture);
    await userEvent.hover(menuItems()[1]);
    await userEvent.keyboard('{Escape}');
    await settleOverlay(fixture);
    expect(menuItems()).toHaveLength(0);

    pressArrowDown(more);
    await settleOverlay(fixture);

    expect(document.activeElement).toBe(menuItems()[0]);
  });

  // 对照：真实移动鼠标时焦点跟随指针，菜单里始终只有一处高亮
  it('moves focus to the item the pointer moves onto', async () => {
    const fixture = await render(560);
    const more = fixture.nativeElement.querySelector(
      '[data-testid="workspace-nav-more"]',
    ) as HTMLElement;
    pressArrowDown(more);
    await settleOverlay(fixture);

    await userEvent.hover(menuItems()[2]);

    expect(document.activeElement).toBe(menuItems()[2]);
  });

  describe('touch targets', () => {
    afterEach(() => setCoarsePointer(false));

    async function openMore(fixture: Awaited<ReturnType<typeof render>>) {
      const more = fixture.nativeElement.querySelector(
        '[data-testid="workspace-nav-more"]',
      ) as HTMLElement;
      more.click();
      await settle(fixture);
      // 浮层带缩放进场动画，量到的尺寸要等动画结束
      await Promise.all(document.getAnimations().map((animation) => animation.finished));
      return { more, menuItems: Array.from(document.querySelectorAll('[hlmDropdownMenuItem]')) };
    }

    it('gives the inline links, the more button and its menu items 44px targets on touch screens', async () => {
      await setCoarsePointer(true);
      const fixture = await render(560);
      const links = inlineLinks(fixture.nativeElement);
      expect(links.length).toBeGreaterThan(0);
      links.forEach(expectTouchTarget);

      const { more, menuItems } = await openMore(fixture);

      expectTouchTarget(more);
      expect(menuItems.length).toBeGreaterThan(0);
      menuItems.forEach(expectTouchTarget);
    });

    it('keeps the compact sizes for a mouse', async () => {
      const fixture = await render(560);

      const { more, menuItems } = await openMore(fixture);

      expect(more.getBoundingClientRect().height).toBeLessThan(44);
      expect(menuItems[0].getBoundingClientRect().height).toBeLessThan(44);
    });
  });
});
//#if (IncludeLocalization)

/**
 * 伪语言按标签容量放宽菜单与「更多」的文案（frontend-i18n.md §5）：放不下的项收进「更多」，
 * 平铺的项单行、连同「更多」不超出导航区，收起的项在菜单里是完整文案。
 */
describe('WorkspaceNav pseudo locale', () => {
  // 侧栏词条里的多词标签当作菜单项：来自本形态的正式词条，长度就是真实菜单的长度；
  // 只取带空格、不以句号结尾的，有断行机会，折行才测得出来
  const pseudo = pseudoCatalog(enTexts);
  const labels = Object.values(pseudo.layout.sidebar).filter(
    (value): value is string =>
      typeof value === 'string' && value.includes(' ') && !/[.。]~*\]$/.test(value),
  );
  const groups = signal<MenuGroup[]>([
    {
      label: pseudo.layout.sidebar.groupWork,
      items: labels.map((label, index) => ({
        label,
        icon: 'lucideGauge',
        route: `/workspace/item-${index}`,
      })),
    },
  ]);

  it('moves what does not fit into the more menu and keeps the rest on one line', async () => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: NavigationService, useValue: { menuGroups: groups, isItemActive: () => false } },
        ...provideTranslocoTesting(),
      ],
    });
    TestBed.inject(TranslocoService).setTranslation(pseudo, 'en');
    const fixture = TestBed.createComponent(TestHost);
    fixture.componentInstance.width.set(900);
    await fixture.whenStable();
    // 网页字体用到时才加载：不等它，量到的是后备字体的宽度（CI 的 Linux 后备字体更宽）
    await document.fonts.ready;
    await new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve)));
    await fixture.whenStable();
    const root: HTMLElement = fixture.nativeElement;

    const links = Array.from(root.querySelectorAll<HTMLElement>('nav a[hlmNavigationMenuLink]'));
    expect(links.length).toBeGreaterThan(1);
    expect(links.length).toBeLessThan(labels.length);
    expect(links.map((link) => link.textContent!.trim())).toEqual(labels.slice(0, links.length));
    for (const link of links) {
      expect(lineCount(link), link.textContent!).toBe(1);
    }

    const more = root.querySelector<HTMLElement>('[data-testid="workspace-nav-more"]')!;
    expect(more.textContent!.trim()).toBe(pseudo.layout.topbar.more);
    expect(lineCount(more)).toBe(1);
    const nav = root.querySelector('nav[hlmNavigationMenu]')!.getBoundingClientRect();
    expect(more.getBoundingClientRect().right).toBeLessThanOrEqual(nav.right + 0.5);
    const box = root.firstElementChild as HTMLElement;
    expect(box.scrollWidth).toBeLessThanOrEqual(box.clientWidth);

    more.click();
    await new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve)));
    await fixture.whenStable();
    await Promise.all(document.getAnimations().map((animation) => animation.finished));
    const folded = Array.from(document.querySelectorAll<HTMLElement>('[hlmDropdownMenuItem]')).map(
      (item) => item.textContent!.trim(),
    );
    expect(folded).toEqual(labels.slice(links.length));
  });
});
//#endif
