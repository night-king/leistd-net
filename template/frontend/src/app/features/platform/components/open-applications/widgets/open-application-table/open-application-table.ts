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
  lucideArrowUpDown,
  lucideChevronRight,
  lucideCircleAlert,
  lucideEllipsis,
  lucideInbox,
  lucidePencil,
  lucideRefreshCw,
  lucideSearchX,
  lucideShield,
  lucideSortAsc,
  lucideSortDesc,
  lucideTrash2,
} from '@ng-icons/lucide';
import { HlmBadge } from '@spartan-ng/helm/badge';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmDropdownMenuImports } from '@spartan-ng/helm/dropdown-menu';
import { HlmSpinner } from '@spartan-ng/helm/spinner';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { HlmTooltipImports } from '@spartan-ng/helm/tooltip';
import { ColumnDef, PaginationState, SortingState } from '@tanstack/angular-table';

import { SettingContextService } from '../../../../../../core/settings/setting-context-service';
import {
  type ReadonlyListItem,
  ReadonlyListPopover,
} from '../../../../../../shared/components/readonly-list-popover/readonly-list-popover';
import { TablePaginator } from '../../../../../../shared/components/table-paginator/table-paginator';
import { ColumnValue } from '../../../../../../shared/directives/column-value';
import { TableFit } from '../../../../../../shared/directives/table-fit';
import { TruncatedTooltip } from '../../../../../../shared/directives/truncated-tooltip';
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
import {
  tableSortAria,
  type TableSortAria,
  tableSortIcon,
  toggleTableSort,
} from '../../../../../../shared/utils/table-sorting';
import { tableViewportSignal } from '../../../../../../shared/utils/table-viewport';
import {
  OpenApplicationOutputDto,
  OpenApplicationType,
} from '../../../../dtos/open-application.dto';

const APPLICATION_TYPE_KEYS: Record<OpenApplicationType, string> = {
  web: 'openApp.appType.web',
  native: 'openApp.appType.native',
  service: 'openApp.appType.service',
};

type BadgeVariant = 'secondary' | 'outline';

@Component({
  selector: 'app-open-application-table',
  imports: [
    NgTemplateOutlet,
    ColumnValue,
    ReadonlyListPopover,
    TableFit,
    TruncatedTooltip,
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
      lucideArrowUpDown,
      lucideChevronRight,
      lucideCircleAlert,
      lucideEllipsis,
      lucideInbox,
      lucidePencil,
      lucideRefreshCw,
      lucideSearchX,
      lucideShield,
      lucideSortAsc,
      lucideSortDesc,
      lucideTrash2,
    }),
  ],
  templateUrl: './open-application-table.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class OpenApplicationTable {
  // 统一按展示时区渲染，避免同一时刻在不同页面显示成不同时间。
  protected readonly displayTimeZone = inject(SettingContextService).timeZone;
  protected readonly displayLocale = inject(SettingContextService).displayLocale;
  //#if (!IncludeLocalization)
  protected readonly t = englishText(ENGLISH);
  //#endif

  readonly applications = input<OpenApplicationOutputDto[]>([]);
  readonly totalCount = input(0);
  readonly pagination = input<PaginationState>({ pageIndex: 0, pageSize: 20 });
  readonly sorting = input<SortingState>([]);
  readonly loading = input(false);
  readonly filtered = input(false);
  /** 加载失败的原因：有值且没有行时显示错误态与重试，与"暂无数据"区分。 */
  readonly loadError = input<string | null>(null);
  readonly retry = output<void>();

  /**
   * 行操作按权限裁剪。默认全开，未启用权限模块的生成物行为不变；
   * 隐藏只影响体验，服务端仍对每个请求独立校验。
   */
  readonly canUpdate = input(true);
  readonly canDelete = input(true);
  readonly canResetSecret = input(true);

  /** 一个可用操作都没有时不渲染溢出菜单，避免留下点开即空的按钮。 */
  readonly hasRowActions = computed(
    () => this.canUpdate() || this.canDelete() || this.canResetSecret(),
  );

  readonly paginationChange = output<PaginationState>();
  readonly sortingChange = output<SortingState>();
  readonly edit = output<string>();
  readonly delete = output<string>();
  readonly resetSecret = output<string>();

  protected readonly tableViewport = tableViewportSignal();
  /** 主列内容外层：最窄一档下长名称截断，不撑宽表格（见 TITLE_COLUMN_META）。 */
  protected readonly titleContentClass = TITLE_CONTENT_CLASS;
  private readonly tableFit = viewChild(TableFit);
  /** 实际折叠档位：视口给上限，容器放不下再降一档（见 TableFit，与容器上的 data-table-fit 同值）。 */
  protected readonly foldLevel = computed(() => this.tableFit()?.level() ?? this.tableViewport());

  protected readonly columns: ColumnDef<AppTableFeatures, OpenApplicationOutputDto>[] = [
    {
      accessorKey: 'clientId',
      id: 'clientId',
      enableHiding: false,
      meta: TITLE_COLUMN_META,
    },
    {
      accessorKey: 'applicationType',
      id: 'type',
      enableSorting: false,
      enableHiding: false,
      meta: { priority: 'primary', locked: true },
    },
    {
      accessorKey: 'permissions',
      id: 'permissions',
      enableSorting: false,
      meta: { priority: 'secondary', detail: true },
    },
    {
      accessorKey: 'redirectUris',
      id: 'redirectUris',
      enableSorting: false,
      meta: { priority: 'tertiary', detail: true },
    },
    {
      accessorKey: 'requirements',
      id: 'security',
      enableSorting: false,
      meta: { priority: 'secondary', detail: true },
    },
    {
      accessorKey: 'creationTime',
      id: 'creationTime',
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

  /** 列标签的词条键：表头与展开详情共用，切换语言时两处一起更新。 */
  protected readonly columnLabelKeys: Readonly<Record<string, string>> = {
    clientId: 'openApp.table.colApp',
    type: 'openApp.table.colType',
    permissions: 'openApp.table.colPermissions',
    redirectUris: 'openApp.table.colRedirectUris',
    security: 'openApp.table.colSecurity',
    creationTime: 'openApp.table.colCreatedAt',
  };

  readonly hasCollapsedColumns = computed(() => this.foldLevel() !== 'desktop');

  /** 展开详情里列出的列：被折叠的数据列，按列定义顺序。 */
  protected detailColumns() {
    return hiddenDetailColumns(this.table);
  }

  protected readonly table = injectAppTable(() => ({
    data: this.applications(),
    columns: this.columns,
    getRowId: (row) => row.id,
    manualPagination: true,
    manualSorting: true,
    rowCount: this.totalCount(),
    onPaginationChange: (updater) =>
      this.paginationChange.emit(resolveTableUpdater(updater, this.pagination())),
    onSortingChange: (updater) =>
      this.sortingChange.emit(resolveTableUpdater(updater, this.sorting())),
    state: {
      columnVisibility: this.columnVisibility(),
      pagination: this.pagination(),
      sorting: this.sorting(),
    },
  }));

  readonly currentPage = computed(() => this.pagination().pageIndex + 1);
  readonly totalPages = computed(() => Math.max(1, this.table.getPageCount()));

  /** 应用类型与同意方式的词条键：取值是封闭联合，模板里经 t 取文案。 */
  protected readonly applicationTypeKeys = APPLICATION_TYPE_KEYS;

  toggleSort(columnId: string): void {
    toggleTableSort(this.table, columnId);
  }

  sortIcon(columnId: string): string {
    return tableSortIcon(this.table, columnId);
  }

  sortAria(columnId: string): TableSortAria {
    return tableSortAria(this.table, columnId);
  }

  /** 每页条数变化：回到第一页并广播新的分页状态。 */
  changePageSize(pageSize: number): void {
    this.paginationChange.emit({ pageIndex: 0, pageSize });
  }

  getVisibleRedirectUris(application: OpenApplicationOutputDto): string[] {
    return application.redirectUris.slice(0, 1);
  }

  getHiddenRedirectUris(application: OpenApplicationOutputDto): string[] {
    return application.redirectUris.slice(1);
  }

  /** 回调地址面板：全部地址，按位置标识（地址可能重复登记）。 */
  getRedirectUriListItems(application: OpenApplicationOutputDto): ReadonlyListItem[] {
    return application.redirectUris.map((uri, index) => ({ id: `${index}`, label: uri }));
  }

  /** 授权能力面板：全部权限，含授权方式以外的端点与范围权限。 */
  getPermissionListItems(application: OpenApplicationOutputDto): ReadonlyListItem[] {
    return application.permissions.map((permission) => ({ id: permission, label: permission }));
  }

  getClientTypeVariant(value: string): BadgeVariant {
    return value === 'public' ? 'secondary' : 'outline';
  }

  /** 已授予的授权方式摘要；一个都没有时为空串，由模板给出"未配置"。 */
  grantSummary(application: OpenApplicationOutputDto): string {
    return application.permissions
      .filter((permission) => permission.startsWith('gt:'))
      .map((permission) => permission.replace('gt:', ''))
      .join(' / ');
  }

  hasPkce(application: OpenApplicationOutputDto): boolean {
    return application.requirements.includes('ft:pkce');
  }
}
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'common.loadFailed': "Couldn't load the list",
  'common.retry': 'Retry',
  'openApp.table.colApp': 'Application',
  'openApp.table.colType': 'Type',
  'openApp.table.colPermissions': 'Capabilities',
  'openApp.table.colSecurity': 'Security',
  'openApp.table.colCreatedAt': 'Created at',
  'common.actions': 'Actions',
  'common.details': 'View details',
  'openApp.permission.notConfigured': 'Not configured',
  'openApp.section.authorization': 'Authorization capabilities',
  'openApp.table.colRedirectUris': 'Redirect URIs',
  'openApp.table.noPkce': 'No PKCE',
  'openApp.table.secretSet': 'Secret set',
  'openApp.table.noSecret': 'No secret',
  'common.edit': 'Edit',
  'openApp.action.resetSecret': 'Reset secret',
  'common.delete': 'Delete',
  'openApp.table.emptyFilteredTitle': 'No matching open applications',
  'openApp.table.emptyFilteredHint': 'Adjust the search or filters',
  'openApp.table.emptyTitle': 'No open applications yet',
  'openApp.table.emptyHint': 'Create a new open application',
  'openApp.table.currentPageReport': '{{total}} total',
  'common.rowsPerPage': 'Items per page',
  'common.pageOf': 'Page {{page}} of {{total}}',
  'common.pagination.first': 'First page',
  'common.pagination.previous': 'Previous page',
  'common.pagination.next': 'Next page',
  'common.pagination.last': 'Last page',
  'openApp.appType.web': 'Web',
  'openApp.appType.native': 'Desktop/Native',
  'openApp.appType.service': 'Service',
  'openApp.popover.permissionsTrigger':
    'View all authorization capabilities of {{name}} ({{count}})',
  'openApp.popover.redirectUrisTrigger': 'View all redirect URIs of {{name}} ({{count}})',
  'openApp.popover.filterPermissions': 'Filter capabilities',
  'openApp.popover.filterRedirectUris': 'Filter redirect URIs',
  'openApp.popover.noMatches': 'No matching entries',
};
//#endif
