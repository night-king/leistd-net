import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  booleanAttribute,
  computed,
  effect,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideCheck, lucideListFilter, lucideSearch } from '@ng-icons/lucide';
import { HlmBadge } from '@spartan-ng/helm/badge';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmInputGroupImports } from '@spartan-ng/helm/input-group';
import { HlmPopoverImports } from '@spartan-ng/helm/popover';
import { HlmSeparatorImports } from '@spartan-ng/helm/separator';

import { PopoverAria } from '../../directives/popover-aria';

/** `icon` 为可选的 ng-icon 名称，由使用方通过 provideIcons 注册。 */
export interface FacetedFilterOption {
  value: string | boolean | null;
  label: string;
  icon?: string;
}

let nextFacetedFilterId = 0;

/**
 * 分面筛选器：虚线描边按钮 + 可搜索选项面板 + 清除。单选（默认）用 `value` / `valueChange`，
 * 选中即关闭；多选（`multiple`）用 `values` / `valuesChange`，连续勾选不关闭。文案由父级以已翻译
 * 字符串传入。
 *
 * 无障碍采用 WAI-ARIA「combobox + listbox」模式并自持键盘导航：`aria-selected` 只表示业务选中，
 * 键盘高亮由 `aria-activedescendant` 表达。不复用 Command 原语，它把 `aria-selected` 绑定为键盘 active。
 */
@Component({
  selector: 'app-faceted-filter',
  standalone: true,
  imports: [
    NgIcon,
    HlmBadge,
    HlmButton,
    ...HlmInputGroupImports,
    ...HlmPopoverImports,
    ...HlmSeparatorImports,
    PopoverAria,
  ],
  providers: [provideIcons({ lucideListFilter, lucideCheck, lucideSearch })],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hlm-popover
      sideOffset="5"
      align="start"
      [state]="state()"
      (stateChanged)="onStateChange($event)"
    >
      <button type="button" hlmBtn hlmPopoverTrigger variant="outline" class="border-dashed">
        <ng-icon name="lucideListFilter" />
        {{ label() }}
        @if (multiple()) {
          @if (selectedOptions().length) {
            <hlm-separator class="mx-2" orientation="vertical" />
            <!-- 最多展示 2 枚 Badge，其余折叠为 +N，防止选中项多时撑宽工具栏。 -->
            <div class="flex gap-1">
              @for (opt of selectedOptions().slice(0, 2); track opt.value) {
                <span hlmBadge variant="secondary">{{ opt.label }}</span>
              }
              @if (selectedOptions().length > 2) {
                <span hlmBadge variant="secondary">+{{ selectedOptions().length - 2 }}</span>
              }
            </div>
          }
        } @else if (selectedOption(); as sel) {
          <hlm-separator class="mx-2" orientation="vertical" />
          <span hlmBadge variant="secondary">{{ sel.label }}</span>
        }
      </button>

      <!-- 面板几何对齐 helm command：容器 w-50 p-0 且不用 flex gap，各段用显式间距，避免隐式 gap 影响清除区。 -->
      <div
        *hlmPopoverPortal
        hlmPopoverContent
        class="w-50 gap-0 overflow-hidden rounded-xl p-0"
        [appPopoverAria]="label()"
      >
        <div class="p-1 pb-0">
          <hlm-input-group
            class="bg-input/30 border-input/30 h-8! rounded-lg! shadow-none! *:data-[slot=input-group-addon]:ps-2!"
          >
            <!-- 普通 input（非 hlmInputGroupInput）：与 helm command-input 一致，聚焦时不额外加边框环。 -->
            <input
              #search
              type="text"
              role="combobox"
              data-slot="command-input"
              class="w-full text-sm outline-hidden disabled:cursor-not-allowed disabled:opacity-50"
              autocomplete="off"
              aria-expanded="true"
              aria-autocomplete="list"
              [attr.aria-controls]="listboxId"
              [attr.aria-activedescendant]="activeOptionId()"
              [attr.aria-label]="searchPlaceholder() || label()"
              [placeholder]="searchPlaceholder() || label()"
              [value]="query()"
              (input)="onQuery($event)"
              (keydown)="onKeydown($event)"
            />
            <hlm-input-group-addon>
              <ng-icon name="lucideSearch" class="shrink-0 text-[length:--spacing(4)] opacity-50" />
            </hlm-input-group-addon>
          </hlm-input-group>
        </div>

        <div
          #listbox
          role="listbox"
          [id]="listboxId"
          [attr.aria-multiselectable]="multiple() ? 'true' : null"
          [attr.aria-label]="label()"
          class="no-scrollbar mt-2.5 max-h-72 scroll-py-1 overflow-x-hidden overflow-y-auto p-1"
        >
          @for (opt of filteredOptions(); track opt.value; let i = $index) {
            <!--
              aria-activedescendant 模式：option 有意不可聚焦（唯一 tab 停靠点是上方 combobox 输入框），
              键盘交互统一由输入框的 keydown 处理，因此此处的可聚焦/键盘事件规则不适用。
            -->
            <!-- eslint-disable-next-line @angular-eslint/template/click-events-have-key-events, @angular-eslint/template/interactive-supports-focus -->
            <div
              role="option"
              [id]="optionId(i)"
              [attr.aria-selected]="isSelected(opt)"
              class="relative flex cursor-default items-center gap-2 rounded-sm px-2 py-1.5 text-sm outline-hidden select-none"
              [class.bg-muted]="i === activeIndex()"
              [class.text-foreground]="i === activeIndex()"
              (click)="onSelect(opt)"
              (mousemove)="activeIndex.set(i)"
            >
              @if (multiple()) {
                <!-- 勾选态为纯视觉图形：选中/未选样式互斥，避免固定暗色类以更高优先级盖掉选中背景。 -->
                <span
                  aria-hidden="true"
                  class="flex size-4 shrink-0 items-center justify-center rounded-[4px] border transition-colors"
                  [class]="
                    isSelected(opt)
                      ? 'border-primary bg-primary text-primary-foreground'
                      : 'border-input dark:bg-input/30'
                  "
                >
                  @if (isSelected(opt)) {
                    <ng-icon name="lucideCheck" class="text-xs" />
                  }
                </span>
              } @else {
                <ng-icon
                  name="lucideCheck"
                  aria-hidden="true"
                  class="text-sm"
                  [class.opacity-0]="!isSelected(opt)"
                />
              }
              @if (opt.icon) {
                <ng-icon [name]="opt.icon" aria-hidden="true" class="text-muted-foreground" />
              }
              <span class="truncate">{{ opt.label }}</span>
            </div>
          }
        </div>
        @if (filteredOptions().length === 0) {
          <!-- 空状态是提示文本而非选项，置于 listbox 之外，保持 listbox 只含 option。 -->
          <div class="py-6 text-center text-sm">{{ emptyLabel() }}</div>
        }

        <!-- 清除是命令而非选项，置于 listbox 之外，避免混入 option 语义。 -->
        @if (hasSelection()) {
          <!-- -mt-1 抵消列表底部内边距，使分隔线紧贴最后一个选项（与 helm command 内的分隔线位置一致）。 -->
          <hlm-separator class="mx-1 -mt-1" />
          <div class="p-1">
            <button
              type="button"
              class="hover:bg-muted hover:text-foreground relative flex w-full cursor-default items-center justify-center rounded-sm px-2 py-1.5 text-sm outline-hidden select-none"
              (click)="clear()"
            >
              {{ clearLabel() }}
            </button>
          </div>
        }
      </div>
    </hlm-popover>
  `,
})
export class FacetedFilter {
  readonly label = input.required<string>();
  readonly options = input.required<FacetedFilterOption[]>();
  readonly value = input<string | boolean | null>(null);
  // 多选开关及其取值（仅字符串值场景，如角色）。
  readonly multiple = input(false, { transform: booleanAttribute });
  readonly values = input<string[]>([]);
  readonly searchPlaceholder = input<string>('');
  readonly clearLabel = input<string>('Clear filter');
  readonly emptyLabel = input<string>('No results');

  readonly valueChange = output<string | boolean | null>();
  readonly valuesChange = output<string[]>();

  readonly state = signal<'open' | 'closed'>('closed');

  private readonly instanceId = nextFacetedFilterId++;
  protected readonly listboxId = `faceted-filter-${this.instanceId}-listbox`;

  private readonly listboxRef = viewChild<ElementRef<HTMLElement>>('listbox');
  protected readonly query = signal('');
  protected readonly activeIndex = signal(0);

  protected readonly filteredOptions = computed(() => {
    const keyword = this.query().trim().toLowerCase();
    const options = this.options();
    return keyword ? options.filter((o) => o.label.toLowerCase().includes(keyword)) : options;
  });

  readonly selectedOption = computed(
    () => this.options().find((o) => o.value === this.value()) ?? null,
  );
  readonly selectedOptions = computed(() =>
    this.options().filter((o) => this.values().includes(String(o.value))),
  );
  readonly hasSelection = computed(() =>
    this.multiple() ? this.selectedOptions().length > 0 : this.selectedOption() !== null,
  );

  protected readonly activeOptionId = computed(() => {
    const count = this.filteredOptions().length;
    return count > 0 ? this.optionId(Math.min(this.activeIndex(), count - 1)) : null;
  });

  constructor() {
    // 过滤结果变化后把高亮重置到首项，避免 aria-activedescendant 指向已消失的选项。
    effect(() => {
      this.filteredOptions();
      this.activeIndex.set(0);
    });
  }

  protected optionId(index: number): string {
    return `faceted-filter-${this.instanceId}-option-${index}`;
  }

  isSelected(option: FacetedFilterOption): boolean {
    return this.multiple()
      ? this.values().includes(String(option.value))
      : option.value === this.value();
  }

  protected onQuery(event: Event): void {
    this.query.set((event.target as HTMLInputElement).value);
  }

  protected onStateChange(state: 'open' | 'closed'): void {
    this.state.set(state);
    if (state === 'closed') {
      this.query.set('');
      this.activeIndex.set(0);
    }
  }

  /** 键盘导航：↑↓ 环绕移动、Home/End 跳边界、Enter 选中；Space 保留给搜索输入。 */
  protected onKeydown(event: KeyboardEvent): void {
    // 输入法合成期间（如中文候选词确认）的 Enter 属于文本输入，不能当作选中。
    if (event.isComposing) {
      return;
    }

    const count = this.filteredOptions().length;
    if (count === 0) {
      return;
    }

    switch (event.key) {
      case 'ArrowDown':
        event.preventDefault();
        this.moveActive((this.activeIndex() + 1) % count);
        break;
      case 'ArrowUp':
        event.preventDefault();
        this.moveActive((this.activeIndex() - 1 + count) % count);
        break;
      case 'Home':
        event.preventDefault();
        this.moveActive(0);
        break;
      case 'End':
        event.preventDefault();
        this.moveActive(count - 1);
        break;
      case 'Enter': {
        event.preventDefault();
        const option = this.filteredOptions()[this.activeIndex()];
        if (option) {
          this.onSelect(option);
        }
        break;
      }
    }
  }

  private moveActive(index: number): void {
    this.activeIndex.set(index);
    this.listboxRef()
      ?.nativeElement.querySelector(`#${CSS.escape(this.optionId(index))}`)
      ?.scrollIntoView({ block: 'nearest' });
  }

  onSelect(option: FacetedFilterOption): void {
    if (this.multiple()) {
      const key = String(option.value);
      const current = this.values();
      const next = current.includes(key) ? current.filter((v) => v !== key) : [...current, key];
      this.valuesChange.emit(next);
      // 多选：保持面板打开，便于连续勾选。
      return;
    }
    this.valueChange.emit(option.value);
    this.state.set('closed');
  }

  clear(): void {
    if (this.multiple()) {
      this.valuesChange.emit([]);
    } else {
      this.valueChange.emit(null);
    }
    this.state.set('closed');
  }
}
