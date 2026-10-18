import { NgTemplateOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  output,
  viewChild,
} from '@angular/core';
//#if (IncludeLocalization)
import { TranslocoDirective } from '@jsverse/transloco';
//#endif
import { NgIcon, provideIcons } from '@ng-icons/core';
import {
  lucideBan,
  lucideBuilding2,
  lucideChevronRight,
  lucideCircleAlert,
  lucideCircleCheck,
  lucideEllipsis,
  lucideInfo,
  //#if (Impersonation)
  lucideLogIn,
  //#endif
  lucidePencil,
  lucideSearchX,
  lucideTrash2,
} from '@ng-icons/lucide';
import { HlmBadge } from '@spartan-ng/helm/badge';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmDropdownMenuImports } from '@spartan-ng/helm/dropdown-menu';
import { HlmSpinner } from '@spartan-ng/helm/spinner';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { HlmTooltipImports } from '@spartan-ng/helm/tooltip';
import { ColumnDef, PaginationState } from '@tanstack/angular-table';

import { SettingContextService } from '../../../../../../core/settings/setting-context-service';
import { TablePaginator } from '../../../../../../shared/components/table-paginator/table-paginator';
import { ColumnValue } from '../../../../../../shared/directives/column-value';
import { StatusDot } from '../../../../../../shared/directives/status-dot';
import { TableFit } from '../../../../../../shared/directives/table-fit';
import { AppDate } from '../../../../../../shared/pipes/app-date-pipe';
import { AppNumber } from '../../../../../../shared/pipes/app-number-pipe';
//#if (!IncludeLocalization)
import { englishText } from '../../../../../../shared/utils/english-text';
//#endif
import {
  ACTIONS_COLUMN_META,
  hiddenDetailColumns,
  tableColumnVisibility,
  TITLE_COLUMN_META,
  TITLE_CONTENT_CLASS,
} from '../../../../../../shared/utils/table-column-meta';
import {
  injectAppTable,
  type AppTableFeatures,
} from '../../../../../../shared/utils/table-features';
import { resolveTableUpdater } from '../../../../../../shared/utils/table-query-state';
import { tableViewportSignal } from '../../../../../../shared/utils/table-viewport';
import { TenantOutputDto } from '../../../../dtos/tenant.dto';

/** 租户列表表格：列优先级驱动响应式收纳，隐藏列由行展开补偿，分页状态由父组件单向下发。 */
@Component({
  selector: 'app-tenant-table',
  imports: [
    NgTemplateOutlet,
    ColumnValue,
    StatusDot,
    TableFit,
    AppDate,
    AppNumber,
    NgIcon,
    HlmBadge,
    HlmButton,
    HlmSpinner,
    TablePaginator,
    ...HlmDropdownMenuImports,
    ...HlmTableImports,
    ...HlmTooltipImports,
    //#if (IncludeLocalization)
    TranslocoDirective,
    //#endif
  ],
  providers: [
    provideIcons({
      lucideBan,
      lucideBuilding2,
      lucideChevronRight,
      lucideCircleAlert,
      lucideCircleCheck,
      lucideEllipsis,
      lucideInfo,
      //#if (Impersonation)
      lucideLogIn,
      //#endif
      lucidePencil,
      lucideSearchX,
      lucideTrash2,
    }),
  ],
  templateUrl: './tenant-table.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TenantTable {
  // 统一按展示时区渲染，避免同一时刻在不同页面显示成不同时间。
  protected readonly displayTimeZone = inject(SettingContextService).timeZone;
  protected readonly displayLocale = inject(SettingContextService).displayLocale;
  //#if (!IncludeLocalization)
  protected readonly t = englishText(ENGLISH);
  //#endif
  readonly tenants = input<TenantOutputDto[]>([]);
  readonly totalCount = input(0);
  readonly pagination = input<PaginationState>({ pageIndex: 0, pageSize: 20 });
  readonly loading = input(false);
  readonly filtered = input(false);
  /** 加载失败的原因：有值且没有行时显示错误态与重试，与"暂无数据"区分。 */
  readonly loadError = input<string | null>(null);
  readonly retry = output<void>();

  /** 行操作按权限裁剪；隐藏只影响体验，服务端仍对每个请求独立校验。 */
  readonly canUpdate = input(true);
  readonly canDelete = input(true);
  //#if (Impersonation)

  /** 模拟登录是宿主侧专属能力（App.Tenants.Impersonation 为 Host 侧别），租户上下文里恒为 false。 */
  readonly canImpersonate = input(true);
  //#endif

  readonly paginationChange = output<PaginationState>();
  readonly details = output<TenantOutputDto>();
  readonly edit = output<TenantOutputDto>();
  readonly toggleActive = output<TenantOutputDto>();
  //#if (Impersonation)
  readonly impersonate = output<TenantOutputDto>();
  //#endif
  readonly delete = output<TenantOutputDto>();

  protected readonly tableViewport = tableViewportSignal();
  /** 主列内容外层：最窄一档下长名称截断，不撑宽表格（见 TITLE_COLUMN_META）。 */
  protected readonly titleContentClass = TITLE_CONTENT_CLASS;
  private readonly tableFit = viewChild(TableFit);
  /** 实际折叠档位：视口给上限，容器放不下再降一档（见 TableFit）。 */
  protected readonly foldLevel = computed(() => this.tableFit()?.level() ?? this.tableViewport());

  // 后端租户列表不支持字段排序（契约只有 offset/limit/keyword），全部列不排序。
  protected readonly columns: ColumnDef<AppTableFeatures, TenantOutputDto>[] = [
    {
      accessorKey: 'name',
      id: 'name',
      enableSorting: false,
      enableHiding: false,
      meta: TITLE_COLUMN_META,
    },
    {
      accessorKey: 'displayName',
      id: 'displayName',
      enableSorting: false,
      meta: { priority: 'secondary', detail: true },
    },
    {
      accessorKey: 'description',
      id: 'description',
      enableSorting: false,
      meta: { priority: 'tertiary', detail: true },
    },
    {
      accessorKey: 'isActive',
      id: 'status',
      enableSorting: false,
      meta: { priority: 'secondary', detail: true },
    },
    {
      accessorKey: 'creationTime',
      id: 'creationTime',
      enableSorting: false,
      meta: { priority: 'tertiary', detail: true },
    },
    {
      id: 'actions',
      enableSorting: false,
      enableHiding: false,
      meta: ACTIONS_COLUMN_META,
    },
  ];

  private readonly columnVisibility = computed(() =>
    tableColumnVisibility(this.columns, this.foldLevel()),
  );

  protected readonly table = injectAppTable(() => ({
    data: this.tenants(),
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

  /** 列标签的词条键：表头与展开详情共用，切换语言时两处一起更新。 */
  protected readonly columnLabelKeys: Readonly<Record<string, string>> = {
    name: 'tenants.colName',
    displayName: 'tenants.colDisplayName',
    description: 'tenants.colDescription',
    status: 'tenants.colStatus',
    creationTime: 'tenants.colCreatedAt',
  };

  readonly hasCollapsedColumns = computed(() => this.foldLevel() !== 'desktop');

  /** 展开详情里列出的列：被折叠的数据列，按列定义顺序。 */
  protected detailColumns() {
    return hiddenDetailColumns(this.table);
  }

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
  'tenants.colName': 'Name',
  'tenants.colDisplayName': 'Display name',
  'tenants.colDescription': 'Description',
  'tenants.colStatus': 'Status',
  'tenants.colCreatedAt': 'Created at',
  'common.actions': 'Actions',
  'common.details': 'View details',
  'tenants.active': 'Active',
  'tenants.inactive': 'Inactive',
  'common.edit': 'Edit',
  'tenants.deactivate': 'Deactivate',
  'tenants.activate': 'Activate',
  //#if (Impersonation)
  'tenants.impersonate': 'Sign in as tenant',
  'tenants.impersonateInactiveHint': 'Deactivated tenants cannot be signed in to',
  //#endif
  'common.delete': 'Delete',
  'tenants.table.emptyFilteredTitle': 'No matching tenants',
  'tenants.table.emptyFilteredHint': 'Adjust the search keyword',
  'tenants.table.emptyTitle': 'No tenants yet',
  'tenants.table.emptyHint': 'Create a new tenant',
  'tenants.table.currentPageReport': '{{total}} in total',
  'common.rowsPerPage': 'Items per page',
  'common.pageOf': 'Page {{page}} of {{total}}',
  'common.pagination.first': 'First page',
  'common.pagination.previous': 'Previous page',
  'common.pagination.next': 'Next page',
  'common.pagination.last': 'Last page',
};
//#endif
