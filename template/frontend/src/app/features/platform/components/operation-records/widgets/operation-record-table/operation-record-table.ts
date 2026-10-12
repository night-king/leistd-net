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
import { SettingContextService } from '../../../../../../core/settings/setting-context-service';
import { TablePaginator } from '../../../../../../shared/components/table-paginator/table-paginator';
import { AppDate } from '../../../../../../shared/pipes/app-date-pipe';
//#if (!IncludeLocalization)
import { englishText } from '../../../../../../shared/utils/english-text';
//#endif
import { tableColumnVisibility } from '../../../../../../shared/utils/table-column-meta';
import {
  injectAppTable,
  type AppTableFeatures,
} from '../../../../../../shared/utils/table-features';
import { resolveTableUpdater } from '../../../../../../shared/utils/table-query-state';
import { tableViewportSignal } from '../../../../../../shared/utils/table-viewport';
import { OperationRecordOutputDto } from '../../../../dtos/operation-record.dto';
//#if (!IncludeLocalization)

/**
 * 失败原因的英文文案，键是失败码。不启用多语言时后端 `failureMessage` 恒为空，须与后端
 * `Api/Resources/en.json` 同步：有 `{码}:Record` 的取它，否则取码本身。`{name}` 占位符用
 * `failureData` 填，键名区分大小写。
 */
const FAILURE_REASONS: Record<string, string> = {
  'Auth:InvalidCredentials':
    'Incorrect username or password (failed attempts in the last {windowMinutes} minutes: {attempts})',
  'Auth:UserTemporarilyLockedOut':
    'Locked out for {minutes} minutes after {maxFailedAttempts} failed sign-in attempts',
  // 框架唯一自产的失败码（RecordDeniedOperationAsync 的默认原因），没有这条会显示裸码
  'Error:Forbidden': 'Not allowed to perform this action.',
  // 再认证失败：改口令、停用两步验证、重发恢复码这三处
  'Security:CurrentPasswordIncorrect': 'The current password is incorrect.',
  'Auth:TwoFactorCodeInvalid': 'The verification code is incorrect.',
};
//#endif

@Component({
  selector: 'app-operation-record-table',
  // 不启用多语言时剩下的 7 项刚好不超 printWidth，prettier 会要求折成一行，与另一生成物冲突；
  // 固定书写形态，与 default-sidebar 的同类数组一致。
  // prettier-ignore
  imports: [
    AppDate,
    NgIcon,
    HlmBadge,
    HlmButton,
    HlmSpinner,
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
  protected readonly displayTimeZone = inject(SettingContextService).timeZone;
  protected readonly displayLocale = inject(SettingContextService).displayLocale;
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
      meta: { priority: 'secondary' },
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

  isColumnHidden(id: string): boolean {
    return this.table.getColumn(id)?.getIsVisible() === false;
  }

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

  readonly currentPage = computed(() => this.pagination().pageIndex + 1);
  readonly totalPages = computed(() => Math.max(1, this.table.getPageCount()));

  isSucceeded(record: OperationRecordOutputDto): boolean {
    return record.outcome === 'succeeded';
  }

  /**
   * 操作人；都缺失时为 undefined，模板显示"匿名"。宿主未下发 name claim 时只有标识。
   * `actorIsTarget`（服务端判定）为真时回落到目标名，前端不按动作码前缀猜。
   */
  actorOf(record: OperationRecordOutputDto): string | undefined {
    const fromTarget = record.actorIsTarget ? record.targetName : undefined;
    return record.actorName ?? record.actorId ?? fromTarget;
  }

  /** 每条记录的操作句子与失败原因，按记录 Id 取；模板里的 t 表达不了"缺词条"，因此在这里选词条。 */
  protected readonly recordTexts = computed(
    () =>
      new Map(
        this.records().map((record) => [
          record.id,
          { sentence: this.actionSentence(record), failure: this.failureReason(record) },
        ]),
      ),
  );

  /** 失败原因：显示后端按当前语言渲染的 `failureMessage`，取不到时回落到原始码。 */
  private failureReason(record: OperationRecordOutputDto): string | null {
    if (!record.failureCode) {
      return null;
    }
    //#if (IncludeLocalization)
    return record.failureMessage ?? record.failureCode;
    //#else
    const text = FAILURE_REASONS[record.failureCode];
    return (
      record.failureMessage ??
      (text ? this.fillPlaceholders(text, this.parseFailureData(record.failureData)) : null) ??
      record.failureCode
    );
    //#endif
  }
  //#if (!IncludeLocalization)

  /** 与后端 `LocalizationPlaceholders.Fill` 同一规则：按名替换 `{name}`，没有对应参数的原样保留。 */
  private fillPlaceholders(text: string, data: Record<string, unknown>): string {
    return text.replace(/\{(\w+)\}/g, (placeholder, name: string) =>
      Object.hasOwn(data, name) ? String(data[name] ?? '') : placeholder,
    );
  }

  /** `failureData` 是后端序列化的 JSON 参数对象；解析失败按无参数处理，一条坏记录不该整行渲染不出来。 */
  private parseFailureData(raw: string | undefined): Record<string, unknown> {
    if (!raw) {
      return {};
    }
    try {
      const parsed: unknown = JSON.parse(raw);
      return parsed && typeof parsed === 'object' ? (parsed as Record<string, unknown>) : {};
    } catch {
      return {};
    }
  }
  //#endif

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
  'operationRecords.table.colTarget': 'Target',
  'operationRecords.table.colFailure': 'Reason',
  'operationRecords.table.colFailureDetail': 'Technical detail',
  'operationRecords.table.colBasis': 'Authorized by',
  'operationRecords.table.colActorTenantId': 'Operator tenant',
  'operationRecords.table.colCorrelationId': 'Trace ID',
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
