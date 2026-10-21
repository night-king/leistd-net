import { DOCUMENT } from '@angular/common';
import {
  afterRenderEffect,
  ChangeDetectionStrategy,
  Component,
  computed,
  ElementRef,
  inject,
  input,
  signal,
  viewChild,
} from '@angular/core';
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideSearch } from '@ng-icons/lucide';
import { HlmInputGroupImports } from '@spartan-ng/helm/input-group';
import { HlmItemImports } from '@spartan-ng/helm/item';
import { HlmPopoverImports } from '@spartan-ng/helm/popover';
import { HlmSeparator } from '@spartan-ng/helm/separator';
import { HlmTooltipImports } from '@spartan-ng/helm/tooltip';

import { PopoverAria } from '../../directives/popover-aria';

/** 列表面板的一行：`label` 是主文字，`detail` 与之不同时在右侧以等宽小字补充（如角色的稳定名称）。 */
export interface ReadonlyListItem {
  readonly id: string;
  readonly label: string;
  readonly detail?: string;
}

let nextId = 0;

/**
 * 只读列表面板：把调用方投影进来的摘要包成一个按钮，点开后在浮层里列出全部条目，条目多时带筛选框。
 * 摘要（几个徽章加 "+N"、授权方式加总数）由调用方决定，投影内容只放非交互元素；总数取 `items` 的长度。
 * 标题区与条目都是一行：标题 · 对象名在左、数量在右；名称在左、补充说明在右，放不下时单行省略（名称优先
 * 占宽），被截断的行鼠标悬停时提示全文，没截断的行不弹。截断只是视觉的，读屏读到的仍是全文；但条目不可
 * 聚焦、提示不响应触摸，键盘与纯触屏用户看不到被截掉的部分（筛选框能缩小范围，不能消除截断），这是本组件
 * 为单行紧凑排版接受的取舍。整个列表是一个可聚焦的滚动区，键盘用方向键、PageUp/PageDown、Home/End 滚动。打开后焦点落到第一个可聚焦元素（有筛选框是筛选框，否则是列表），
 * Esc 关闭并回到按钮，都由 Brain 完成。
 *
 * ```html
 * <app-readonly-list-popover [items]="roles" [triggerLabel]="..." [title]="..." [subtitle]="user.name"
 *   [filterLabel]="..." [emptyText]="...">
 *   <span hlmBadge variant="outline">+3</span>
 * </app-readonly-list-popover>
 * ```
 */
@Component({
  selector: 'app-readonly-list-popover',
  imports: [
    NgIcon,
    HlmSeparator,
    PopoverAria,
    ...HlmInputGroupImports,
    ...HlmItemImports,
    ...HlmPopoverImports,
    ...HlmTooltipImports,
  ],
  providers: [provideIcons({ lucideSearch })],
  templateUrl: './readonly-list-popover.html',
  // 宿主与按钮都可收缩：摘要里的名称各自截断，数量由调用方设为不收缩；宽度上限由调用方加在宿主上。
  // `title` 是面板标题的输入，不让它同时落成宿主的原生提示
  host: { class: 'flex min-w-0', '[attr.title]': 'null' },
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ReadonlyListPopover {
  readonly items = input.required<readonly ReadonlyListItem[]>();
  /** 按钮的可访问名，说清点开会看到什么（如"查看 X 的全部角色（5 个）"）。 */
  readonly triggerLabel = input.required<string>();
  /** 面板标题，也是面板与列表的可访问名；数量由组件显示在标题右侧。 */
  readonly title = input.required<string>();
  readonly subtitle = input<string>();
  readonly filterLabel = input.required<string>();
  /** 筛选无结果时的提示。 */
  readonly emptyText = input.required<string>();
  /** 条目数达到该值才显示筛选框。 */
  readonly filterThreshold = input(7);
  /** 条目是地址、权限名这类标识时用等宽字体。 */
  readonly mono = input(false);

  protected readonly filterId = `readonly-list-filter-${nextId++}`;
  /** 底部渐隐：列表还能往下滚时，最后 1.5rem 由不透明过渡到透明；滚到底即撤掉，不遮挡末项。 */
  protected readonly fadeMask =
    'linear-gradient(to bottom, black calc(100% - 1.5rem), transparent)';

  protected readonly query = signal('');
  protected readonly showFilter = computed(() => this.items().length >= this.filterThreshold());

  protected readonly visibleItems = computed(() => {
    const query = this.query().trim().toLocaleLowerCase();
    if (!query || !this.showFilter()) {
      return this.items();
    }
    return this.items().filter(
      (item) =>
        item.label.toLocaleLowerCase().includes(query) ||
        (item.detail?.toLocaleLowerCase().includes(query) ?? false),
    );
  });

  protected readonly fadeBottom = signal(false);
  /** 名称或补充说明被截断的条目：只有它们悬停时提示全文。 */
  protected readonly truncated = signal<ReadonlySet<string>>(new Set());

  private readonly list = viewChild<ElementRef<HTMLElement>>('list');
  private readonly fontsLoaded = signal(false);

  constructor() {
    // 文字宽度随字体就绪而变，就绪后再量一次
    void inject(DOCUMENT).fonts?.ready.then(() => this.fontsLoaded.set(true));
    // 面板打开（列表出现）、条目变化或字体就绪后量一次；打开期间视口缩放、旋转改变列表宽度时由
    // ResizeObserver 重量。列表随面板关闭而销毁，观察随之断开
    afterRenderEffect((onCleanup) => {
      this.visibleItems();
      this.fontsLoaded();
      const element = this.list()?.nativeElement;
      if (!element) {
        return;
      }
      this.measure(element);
      const observer = new ResizeObserver(() => this.measure(element));
      observer.observe(element);
      onCleanup(() => observer.disconnect());
    });
  }

  protected hasDetail(item: ReadonlyListItem): boolean {
    return !!item.detail && item.detail !== item.label;
  }

  /** 悬停提示的全文：名称加补充说明。 */
  protected fullTextOf(item: ReadonlyListItem): string {
    return this.hasDetail(item) ? `${item.label} · ${item.detail}` : item.label;
  }

  protected updateFade(element: HTMLElement): void {
    this.fadeBottom.set(element.scrollHeight - element.scrollTop - element.clientHeight > 1);
  }

  protected onFilterInput(event: Event): void {
    this.query.set((event.target as HTMLInputElement).value);
  }

  /** 面板关闭时清空筛选：下次打开从全部条目开始。 */
  protected reset(): void {
    this.query.set('');
    this.fadeBottom.set(false);
    this.truncated.set(new Set());
  }

  /** 渐隐与截断一起量：左右两列各自判断，单量整行量不出子节点自身的省略。 */
  private measure(list: HTMLElement): void {
    this.updateFade(list);
    const ids = new Set<string>();
    for (const row of Array.from(list.querySelectorAll<HTMLElement>('[data-item-id]'))) {
      const cells = row.querySelectorAll<HTMLElement>(
        '[data-readonly-list-label], [data-readonly-list-detail]',
      );
      if (Array.from(cells).some((cell) => cell.scrollWidth > cell.clientWidth)) {
        ids.add(row.dataset['itemId']!);
      }
    }
    const current = this.truncated();
    if (ids.size !== current.size || [...ids].some((id) => !current.has(id))) {
      this.truncated.set(ids);
    }
  }
}
