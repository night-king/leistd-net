import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { NgIcon, provideIcons } from '@ng-icons/core';
import {
  lucideChevronFirst,
  lucideChevronLast,
  lucideChevronLeft,
  lucideChevronRight,
} from '@ng-icons/lucide';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmSelectImports } from '@spartan-ng/helm/select';
import { HlmTooltipImports } from '@spartan-ng/helm/tooltip';

/** 分页栏文案（均为已翻译字符串，由父级传入）。 */
export interface TablePaginatorLabels {
  currentPageReport: string;
  rowsPerPage: string;
  page: string;
  first: string;
  previous: string;
  next: string;
  last: string;
}

/**
 * 表格分页栏：当前页信息 + 每页条数 + 首/上/下/末页导航。分页状态由父表格持有，本组件只渲染并回传
 * 导航意图；文案由父级以已翻译字符串传入。
 */
@Component({
  selector: 'app-table-paginator',
  standalone: true,
  host: { class: 'flex flex-wrap items-center justify-between gap-x-4 gap-y-2 text-sm' },
  imports: [NgIcon, HlmButton, ...HlmSelectImports, ...HlmTooltipImports],
  providers: [
    provideIcons({
      lucideChevronFirst,
      lucideChevronLeft,
      lucideChevronRight,
      lucideChevronLast,
    }),
  ],
  templateUrl: './table-paginator.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TablePaginator {
  readonly labels = input.required<TablePaginatorLabels>();
  readonly rows = input.required<number>();
  readonly rowsPerPageOptions = input<number[]>([10, 20, 50, 100]);
  readonly canPrev = input.required<boolean>();
  readonly canNext = input.required<boolean>();

  readonly firstPage = output<void>();
  readonly prevPage = output<void>();
  readonly nextPage = output<void>();
  readonly lastPage = output<void>();
  readonly rowsChange = output<number>();

  onRowsChange(value: number | null | undefined): void {
    if (value == null) {
      return;
    }
    this.rowsChange.emit(value);
  }
}
