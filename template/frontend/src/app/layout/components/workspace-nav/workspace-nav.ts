import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  afterNextRender,
  computed,
  inject,
  input,
  signal,
  viewChild,
} from '@angular/core';
import { RouterLink } from '@angular/router';
//#if (IncludeLocalization)
import { TranslocoDirective } from '@jsverse/transloco';
//#endif
import { NgIcon, provideIcons } from '@ng-icons/core';
import {
  lucideChevronDown,
  lucideGauge,
  lucideLayers,
  lucideMenu,
  lucideSettings,
} from '@ng-icons/lucide';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmDropdownMenuImports } from '@spartan-ng/helm/dropdown-menu';
import { HlmNavigationMenuImports } from '@spartan-ng/helm/navigation-menu';
import { HlmSheetImports } from '@spartan-ng/helm/sheet';
import { HlmTooltipImports } from '@spartan-ng/helm/tooltip';

import { LayoutService } from '../../../core/services/layout-service';
import { Logo } from '../../../shared/components/logo/logo';
//#if (!IncludeLocalization)
import { englishText } from '../../../shared/utils/english-text';
//#endif
import { MenuItem, NavigationService } from '../../services/navigation-service';

/**
 * 工作空间顶栏的导航：按菜单分组的 `placement` 放在左侧（文字链接）或右侧（图标按钮），不显示分组标题；
 * 窄屏只剩左侧的抽屉按钮。新增菜单项用了新图标时在这里的 `provideIcons` 登记。
 *
 * 左侧链接不换行：按导航区的实际宽度从前往后排，放不下的收进末尾的「更多」菜单。宽度来自一份
 * 不可见的量尺（全部标签加「更多」按钮），换语言、增减菜单项时量尺跟着变宽变窄，无需另行监听。
 */
@Component({
  selector: 'app-workspace-nav',
  // prettier-ignore
  imports: [
    RouterLink,
    NgIcon,
    HlmButton,
    ...HlmDropdownMenuImports,
    ...HlmNavigationMenuImports,
    ...HlmSheetImports,
    ...HlmTooltipImports,
    Logo,
    //#if (IncludeLocalization)
    TranslocoDirective,
    //#endif
  ],
  providers: [
    provideIcons({ lucideChevronDown, lucideGauge, lucideLayers, lucideMenu, lucideSettings }),
  ],
  templateUrl: './workspace-nav.html',
  host: { class: 'flex min-w-0 items-center gap-3', '[class.flex-1]': "placement() === 'start'" },
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class WorkspaceNav {
  readonly placement = input<'start' | 'end'>('start');

  readonly layoutService = inject(LayoutService);
  private readonly navigation = inject(NavigationService);
  //#if (!IncludeLocalization)
  protected readonly t = englishText(ENGLISH);
  //#endif

  readonly groups = this.navigation.menuGroups;

  private readonly ownGroups = computed(() =>
    this.groups().filter((group) => (group.placement ?? 'start') === this.placement()),
  );

  readonly items = computed(() => this.ownGroups().flatMap((group) => group.items));

  /** 右侧导航区的可访问名。左右两个导航区不能同名，否则读屏器听到两个"导航"；右侧用它自己的分组名。 */
  readonly endNavLabel = computed(() =>
    this.ownGroups()
      .map((group) => group.label)
      .join(' / '),
  );

  private readonly navRef = viewChild<ElementRef<HTMLElement>>('nav');
  private readonly rulerRef = viewChild<ElementRef<HTMLElement>>('ruler');
  private readonly available = signal(0);
  private readonly ruler = signal<RulerWidths>({ items: [], more: 0, gap: 0 });

  /** 左侧平铺显示的项数，其余进「更多」菜单。量出宽度之前全部平铺。 */
  readonly visibleCount = computed(() =>
    fitCount(this.available(), this.ruler(), this.items().length),
  );
  readonly inlineItems = computed(() => this.items().slice(0, this.visibleCount()));
  readonly overflowItems = computed(() => this.items().slice(this.visibleCount()));
  /** 当前页在「更多」菜单里时，触发按钮按当前项着色，免得导航上看不出身在何处。 */
  readonly overflowActive = computed(() =>
    this.overflowItems().some((item) => this.isItemActive(item)),
  );

  constructor() {
    const resize = new ResizeObserver(() => this.measure());
    afterNextRender(() => {
      const nav = this.navRef()?.nativeElement;
      const ruler = this.rulerRef()?.nativeElement;
      if (nav && ruler) {
        resize.observe(nav);
        resize.observe(ruler);
      }
    });
    inject(DestroyRef).onDestroy(() => resize.disconnect());
  }

  isItemActive(item: MenuItem): boolean {
    return this.navigation.isItemActive(item);
  }

  private measure(): void {
    const nav = this.navRef()?.nativeElement;
    const ruler = this.rulerRef()?.nativeElement;
    if (!nav || !ruler) {
      return;
    }
    const widths = Array.from(ruler.children, (child) => child.getBoundingClientRect().width);
    this.available.set(nav.clientWidth);
    this.ruler.set({
      items: widths.slice(0, -1),
      more: widths.at(-1) ?? 0,
      gap: parseFloat(getComputedStyle(ruler).columnGap) || 0,
    });
  }
}

/** 量尺上各项的宽度：菜单项依次排列，`more` 是「更多」按钮，`gap` 是项间距。 */
export interface RulerWidths {
  items: readonly number[];
  more: number;
  gap: number;
}

/**
 * 在 `available` 宽度里能平铺几项：全部放得下就全放；否则从前往后排，并给「更多」按钮留出位置。
 * 尚未量出宽度（导航区隐藏、量尺与菜单项数对不上）时返回全部项数。
 */
export function fitCount(available: number, ruler: RulerWidths, total: number): number {
  if (available <= 0 || ruler.items.length !== total) {
    return total;
  }
  const all = ruler.items.reduce((sum, width) => sum + width, 0) + ruler.gap * (total - 1);
  if (all <= available) {
    return total;
  }
  let used = ruler.more;
  let count = 0;
  for (const width of ruler.items) {
    if (used + ruler.gap + width > available) {
      break;
    }
    used += ruler.gap + width;
    count++;
  }
  return count;
}
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'layout.topbar.openNavigation': 'Open navigation',
  'layout.sidebar.appTitle': 'Template Project',
  'layout.topbar.navigationDescription': 'Choose a page to open',
  'layout.sidebar.navigation': 'Navigation',
  'layout.topbar.more': 'More',
};
//#endif
