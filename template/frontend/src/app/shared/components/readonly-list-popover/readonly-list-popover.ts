import {
  afterRenderEffect,
  ChangeDetectionStrategy,
  Component,
  computed,
  ElementRef,
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

import { PopoverAria } from '../../directives/popover-aria';

/** 列表面板的一行：`label` 是主文字，`detail` 与之不同时在下面以等宽小字补充（如角色的稳定名称）。 */
export interface ReadonlyListItem {
  readonly id: string;
  readonly label: string;
  readonly detail?: string;
}

let nextId = 0;

/**
 * 只读列表面板：把调用方投影进来的摘要包成一个按钮，点开后在浮层里列出全部条目，条目多时带筛选框。
 * 摘要（几个徽章加 "+N"、授权方式加总数）由调用方决定，投影内容只放非交互元素；总数取 `items` 的长度。
 * 条目是只读行，名称与补充说明都折行显示，不截断；整个列表是一个可聚焦的滚动区，键盘用方向键、
 * PageUp/PageDown、Home/End 滚动。打开后焦点落到第一个可聚焦元素（有筛选框是筛选框，否则是列表），
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

  private readonly list = viewChild<ElementRef<HTMLElement>>('list');

  constructor() {
    afterRenderEffect(() => {
      this.visibleItems();
      const element = this.list()?.nativeElement;
      if (element) {
        this.updateFade(element);
      }
    });
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
  }
}
