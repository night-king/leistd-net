import {
  ChangeDetectorRef,
  DestroyRef,
  Directive,
  ElementRef,
  afterEveryRender,
  afterNextRender,
  computed,
  inject,
  input,
  signal,
} from '@angular/core';

import { TableViewport } from '../utils/table-column-meta';

/** 列折叠档位从宽到窄：desktop 显示全部列，tablet 收起 tertiary，mobile 只留 primary。 */
const LEVELS: readonly TableViewport[] = ['desktop', 'tablet', 'mobile'];

interface FitBasis {
  width: number;
  ceiling: TableViewport;
  content: unknown;
  lang: string;
}

/**
 * 按表格容器的实际宽度决定列折叠档位，挂在包住表格的容器上。视口档位（`tableViewportSignal()`）
 * 给出上限，容器放不下全部可见列时再降一档，降下的列进展开详情：表格宽度还取决于列内容与侧栏状态。
 *
 * 容器宽度、视口上限、表格数据或界面语言变化时，先回到上限再逐档降到放得下，在同一轮渲染里完成。
 * 实际档位写在容器的 `data-table-fit` 上；最窄一档时主列（`TITLE_COLUMN_META`）占满剩余宽度并截断。
 */
@Directive({
  selector: '[appTableFit]',
  exportAs: 'appTableFit',
  host: { '[attr.data-table-fit]': 'level()' },
})
export class TableFit {
  /**
   * 视口档位，作为折叠的上限。不设为必填：宿主表格在自身的 computed 里读 `level`，
   * 视图首次检查时绑定可能还没写入，必填输入此时会抛 NG0950。
   */
  readonly ceiling = input<TableViewport>('desktop', { alias: 'appTableFit' });
  /** 表格数据：换数据后被收起的列也可能变宽或变窄，须从上限重新判断。 */
  readonly content = input<unknown>(undefined, { alias: 'appTableFitContent' });

  private readonly host: HTMLElement = inject(ElementRef).nativeElement;
  private readonly fitted = signal<TableViewport>('desktop');
  /** 容器宽度由 ResizeObserver 写入；只看宽度，行数变化引起的高度变化不算。 */
  private width = 0;
  /** 上一次判断所依据的事实；与当前不同就从上限重新判断。 */
  private basis: FitBasis | undefined;

  /** 实际采用的档位：视口上限与容器能容纳的档位取较窄的一个。 */
  readonly level = computed(() => narrower(this.ceiling(), this.fitted()));

  constructor() {
    // 容器尺寸与界面语言的变化不一定引起 Angular 渲染，这里安排一次，交给下面的检查
    const changeDetector = inject(ChangeDetectorRef);
    const resize = new ResizeObserver(([entry]) => {
      if (entry.contentRect.width !== this.width) {
        this.width = entry.contentRect.width;
        changeDetector.markForCheck();
      }
    });
    const language = new MutationObserver(() => changeDetector.markForCheck());
    afterNextRender(() => {
      resize.observe(this.host);
      language.observe(document.documentElement, { attributeFilter: ['lang'] });
    });
    inject(DestroyRef).onDestroy(() => {
      resize.disconnect();
      language.disconnect();
    });

    // 唯一的检查入口，每次渲染后一次：表格在 @defer 块里晚于容器出现，换数据、换语言也只表现为
    // 一次普通渲染。改了档位会再渲染一轮，下一次检查读到的才是按新档位排好的表格——
    // 同一阶段里再检查一遍会读到旧宽度，多降一档。
    afterEveryRender({ read: () => this.check() });
  }

  private check(): void {
    const table = this.host.querySelector('table');
    const scroller = table?.parentElement;
    const available = scroller?.clientWidth ?? 0;
    if (!table || !available) {
      return;
    }

    const basis: FitBasis = {
      width: this.width,
      ceiling: this.ceiling(),
      content: this.content(),
      lang: document.documentElement.lang,
    };
    const changed = this.basis !== undefined && !sameBasis(this.basis, basis);
    this.basis = basis;
    if (changed && this.fitted() !== 'desktop') {
      this.fitted.set('desktop');
      return;
    }

    if (table.scrollWidth > available) {
      const next = LEVELS[LEVELS.indexOf(this.level()) + 1];
      if (next) {
        this.fitted.set(next);
      }
    }
  }
}

function sameBasis(a: FitBasis, b: FitBasis): boolean {
  return (
    a.width === b.width && a.ceiling === b.ceiling && a.content === b.content && a.lang === b.lang
  );
}

function narrower(a: TableViewport, b: TableViewport): TableViewport {
  return LEVELS.indexOf(a) >= LEVELS.indexOf(b) ? a : b;
}
