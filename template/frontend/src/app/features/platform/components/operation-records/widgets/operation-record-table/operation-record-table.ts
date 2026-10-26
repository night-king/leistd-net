import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, input, output } from '@angular/core';
//#if (IncludeLocalization)
import { TranslocoDirective } from '@jsverse/transloco';
//#endif
import { NgIcon, provideIcons } from '@ng-icons/core';
import {
  lucideChevronRight,
  lucideCircleAlert,
  lucideDatabase,
  lucideSearchX,
  lucideUserPen,
} from '@ng-icons/lucide';
import { HlmBadge } from '@spartan-ng/helm/badge';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmSpinner } from '@spartan-ng/helm/spinner';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { ColumnDef, PaginationState } from '@tanstack/angular-table';

import { injectOperationActionSentence } from './operation-action-sentence';
import { AccountPreferencesService } from '../../../../../../core/services/account-preferences-service';
import { TablePaginator } from '../../../../../../shared/components/table-paginator/table-paginator';
import { ColumnValue } from '../../../../../../shared/directives/column-value';
import { StatusDot } from '../../../../../../shared/directives/status-dot';
import { AppDate } from '../../../../../../shared/pipes/app-date-pipe';
import { AppNumber } from '../../../../../../shared/pipes/app-number-pipe';
//#if (!IncludeLocalization)
import { englishText } from '../../../../../../shared/utils/english-text';
//#endif
import {
  hiddenDetailColumns,
  tableColumnVisibility,
} from '../../../../../../shared/utils/table-column-meta';
import {
  injectAppTable,
  type AppTableFeatures,
} from '../../../../../../shared/utils/table-features';
import { resolveTableUpdater } from '../../../../../../shared/utils/table-query-state';
import { tableViewportSignal } from '../../../../../../shared/utils/table-viewport';
import { OperationRecordOutputDto } from '../../../../dtos/operation-record.dto';
import { OperationRecordDetail } from '../operation-record-detail/operation-record-detail';
import { operationRecordActor } from '../operation-record-detail/operation-record-texts';

@Component({
  selector: 'app-operation-record-table',
  imports: [
    NgTemplateOutlet,
    ColumnValue,
    StatusDot,
    AppDate,
    AppNumber,
    NgIcon,
    HlmBadge,
    HlmButton,
    HlmSpinner,
    OperationRecordDetail,
    TablePaginator,
    ...HlmTableImports,
    //#if (IncludeLocalization)
    TranslocoDirective,
    //#endif
  ],
  providers: [
    provideIcons({
      lucideChevronRight,
      lucideCircleAlert,
      lucideDatabase,
      lucideSearchX,
      lucideUserPen,
    }),
  ],
  templateUrl: './operation-record-table.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class OperationRecordTable {
  // 统一按展示时区渲染，避免同一时刻在不同页面显示成不同时间。
  protected readonly displayTimeZone = inject(AccountPreferencesService).timeZone;
  protected readonly displayLocale = inject(AccountPreferencesService).displayLocale;
  //#if (!IncludeLocalization)
  protected readonly t = englishText(ENGLISH);
  //#endif
  private readonly actionSentence = injectOperationActionSentence();

  readonly records = input<OperationRecordOutputDto[]>([]);
  readonly totalCount = input(0);
  readonly pagination = input<PaginationState>({ pageIndex: 0, pageSize: 20 });
  readonly loading = input(false);
  readonly filtered = input(false);
  /** 加载失败的原因：有值且没有行时显示错误态与重试，与"暂无数据"区分。 */
  readonly loadError = input<string | null>(null);
  readonly retry = output<void>();

  readonly paginationChange = output<PaginationState>();

  private readonly tableViewport = tableViewportSignal();

  /**
   * 四列：时间、操作人、操作内容、结果。句子不含操作人，避免同一名字在一行出现两遍；成败独立成列
   * 才能单独筛选与告警（NIST AU-3）。目标标识、授权依据、链路标识放展开区；本页只读，没有操作列。
   */
  protected readonly columns: ColumnDef<AppTableFeatures, OperationRecordOutputDto>[] = [
    {
      accessorKey: 'creationTime',
      id: 'creationTime',
      enableSorting: false,
      enableHiding: false,
      meta: { priority: 'primary', locked: true },
    },
    {
      accessorKey: 'actorName',
      id: 'actor',
      enableSorting: false,
      meta: { priority: 'secondary', detail: true },
    },
    {
      accessorKey: 'action',
      id: 'action',
      enableSorting: false,
      enableHiding: false,
      meta: { priority: 'primary', locked: true },
    },
    {
      accessorKey: 'outcome',
      id: 'outcome',
      enableSorting: false,
      enableHiding: false,
      meta: { priority: 'primary', locked: true },
    },
  ];

  private readonly columnVisibility = computed(() =>
    tableColumnVisibility(this.columns, this.tableViewport()),
  );

  protected readonly table = injectAppTable(() => ({
    data: this.records(),
    columns: this.columns,
    getRowId: (row) => row.id,
    manualPagination: true,
    rowCount: this.totalCount(),
    onPaginationChange: (updater) =>
      this.paginationChange.emit(resolveTableUpdater(updater, this.pagination())),
    state: {
      columnVisibility: this.columnVisibility(),
      pagination: this.pagination(),
    },
  }));

  /** 列标签的词条键：表头与展开详情共用，切换语言时两处一起更新。 */
  protected readonly columnLabelKeys: Readonly<Record<string, string>> = {
    creationTime: 'operationRecords.table.colTime',
    actor: 'operationRecords.table.colActor',
    action: 'operationRecords.table.colAction',
    outcome: 'operationRecords.table.colOutcome',
  };

  /** 展开详情里列出的列：被折叠的数据列，按列定义顺序；追查字段由记录详情接在后面。 */
  protected detailColumns() {
    return hiddenDetailColumns(this.table);
  }

  readonly currentPage = computed(() => this.pagination().pageIndex + 1);
  readonly totalPages = computed(() => Math.max(1, this.table.getPageCount()));

  isSucceeded(record: OperationRecordOutputDto): boolean {
    return record.outcome === 'succeeded';
  }

  /** 操作人；都缺失时模板显示"匿名"，回落规则见 {@link operationRecordActor}。 */
  actorOf(record: OperationRecordOutputDto): string | undefined {
    return operationRecordActor(record);
  }

  /** 每条记录的操作句子，按记录 Id 取；模板里的 t 表达不了"缺词条"，因此在这里选词条。 */
  protected readonly recordTexts = computed(
    () =>
      new Map(
        this.records().map((record) => [record.id, { sentence: this.actionSentence(record) }]),
      ),
  );

  /** 每页条数变化：回到第一页并广播新的分页状态。 */
  changePageSize(pageSize: number): void {
    this.paginationChange.emit({ pageIndex: 0, pageSize });
  }
}
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'common.loadFailed': "Couldn't load the list",
  'common.retry': 'Retry',
  'operationRecords.table.colAction': 'Action',
  'operationRecords.table.colOutcome': 'Outcome',
  'operationRecords.table.colActor': 'Operator',
  'operationRecords.table.colTime': 'Time',
  'common.details': 'View details',
  'operationRecords.outcome.succeeded': 'Succeeded',
  'operationRecords.outcome.failed': 'Rejected',
  'operationRecords.table.unknownActor': 'Anonymous',
  'operationRecords.table.impersonatedBy': 'acting on behalf, by {{name}}',
  'operationRecords.table.emptyFilteredTitle': 'No matching records',
  'operationRecords.table.emptyFilteredHint': 'Adjust the search',
  'operationRecords.table.emptyTitle': 'No operation records yet',
  'operationRecords.table.emptyHint': 'Records appear here as soon as an audited operation runs',
  'operationRecords.table.currentPageReport': '{{total}} in total',
  'common.rowsPerPage': 'Items per page',
  'common.pageOf': 'Page {{page}} of {{total}}',
  'common.pagination.first': 'First page',
  'common.pagination.previous': 'Previous page',
  'common.pagination.next': 'Next page',
  'common.pagination.last': 'Last page',
};
//#endif
