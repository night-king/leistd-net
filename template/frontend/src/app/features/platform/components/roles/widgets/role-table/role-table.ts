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
  lucideKeyRound,
  lucidePencil,
  lucideSearchX,
  lucideShieldCheck,
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
import { TablePaginator } from '../../../../../../shared/components/table-paginator/table-paginator';
import { ColumnValue } from '../../../../../../shared/directives/column-value';
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
import {
  tableSortAria,
  type TableSortAria,
  tableSortIcon,
  toggleTableSort,
} from '../../../../../../shared/utils/table-sorting';
import { tableViewportSignal } from '../../../../../../shared/utils/table-viewport';
import { RoleOutputDto } from '../../../../dtos/role.dto';

/** 角色列表表格：列优先级驱动响应式收纳，隐藏列由行展开补偿，分页与排序状态由父组件单向下发。 */
@Component({
  selector: 'app-role-table',
  imports: [
    NgTemplateOutlet,
    ColumnValue,
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
      lucideArrowUpDown,
      lucideChevronRight,
      lucideCircleAlert,
      lucideEllipsis,
      lucideKeyRound,
      lucidePencil,
      lucideSearchX,
      lucideShieldCheck,
      lucideSortAsc,
      lucideSortDesc,
      lucideTrash2,
    }),
  ],
  templateUrl: './role-table.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RoleTable {
  // 统一按展示时区渲染，避免同一时刻在不同页面显示成不同时间。
  protected readonly displayTimeZone = inject(SettingContextService).timeZone;
  protected readonly displayLocale = inject(SettingContextService).displayLocale;
  //#if (!IncludeLocalization)
  protected readonly t = englishText(ENGLISH);

  //#endif
  readonly deletingId = input<string | null>(null);
  readonly roles = input<RoleOutputDto[]>([]);
  readonly totalCount = input(0);
  readonly pagination = input<PaginationState>({ pageIndex: 0, pageSize: 20 });
  readonly sorting = input<SortingState>([]);
  readonly loading = input(false);
  readonly filtered = input(false);
  /** 加载失败的原因：有值且没有行时显示错误态与重试，与"暂无数据"区分。 */
  readonly loadError = input<string | null>(null);
  readonly retry = output<void>();

  /** 行操作按权限裁剪；隐藏只影响体验，服务端仍对每个请求独立校验。 */
  readonly canUpdate = input(true);
  readonly canDelete = input(true);
  readonly canManagePermissions = input(false);

  /** 一个可用操作都没有时不渲染溢出菜单，避免留下点开即空的按钮。 */
  readonly hasRowActions = computed(
    () => this.canUpdate() || this.canDelete() || this.canManagePermissions(),
  );

  readonly paginationChange = output<PaginationState>();
  readonly sortingChange = output<SortingState>();
  readonly edit = output<RoleOutputDto>();
  readonly delete = output<RoleOutputDto>();
  readonly managePermissions = output<RoleOutputDto>();

  protected readonly tableViewport = tableViewportSignal();
  /** 主列内容外层：最窄一档下长名称截断，不撑宽表格（见 TITLE_COLUMN_META）。 */
  protected readonly titleContentClass = TITLE_CONTENT_CLASS;
  private readonly tableFit = viewChild(TableFit);
  /** 实际折叠档位：视口给上限，容器放不下再降一档（见 TableFit）。 */
  protected readonly foldLevel = computed(() => this.tableFit()?.level() ?? this.tableViewport());

  protected readonly columns: ColumnDef<AppTableFeatures, RoleOutputDto>[] = [
    {
      accessorKey: 'displayName',
      id: 'displayName',
      enableHiding: false,
      meta: TITLE_COLUMN_META,
    },
    {
      accessorKey: 'userCount',
      id: 'userCount',
      enableSorting: false,
      meta: { priority: 'secondary', detail: true },
    },
    {
      accessorKey: 'permissionCount',
      id: 'permissionCount',
      enableSorting: false,
      meta: { priority: 'secondary', detail: true },
    },
    {
      accessorKey: 'sort',
      id: 'sort',
      meta: { priority: 'tertiary', detail: true },
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

  protected readonly table = injectAppTable(() => ({
    data: this.roles(),
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

  /** 列标签的词条键：表头与展开详情共用，切换语言时两处一起更新。 */
  protected readonly columnLabelKeys: Readonly<Record<string, string>> = {
    displayName: 'roles.colName',
    userCount: 'roles.colUsers',
    permissionCount: 'roles.colPermissions',
    sort: 'roles.colSort',
    creationTime: 'roles.colCreatedAt',
  };

  readonly hasCollapsedColumns = computed(() => this.foldLevel() !== 'desktop');

  /** 展开详情里列出的列：被折叠的数据列，按列定义顺序。 */
  protected detailColumns() {
    return hiddenDetailColumns(this.table);
  }

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
}
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'common.loadFailed': "Couldn't load the list",
  'common.retry': 'Retry',
  'roles.colName': 'Role',
  'roles.colUsers': 'Users',
  'roles.colPermissions': 'Permissions',
  'roles.colSort': 'Sort',
  'roles.colCreatedAt': 'Created at',
  'common.actions': 'Actions',
  'common.details': 'View details',
  'roles.static': 'Built-in',
  'roles.default': 'Default',
  'roles.configurePermissions': 'Configure permissions',
  'common.edit': 'Edit',
  'roles.staticRoleHint': 'Built-in roles cannot be deleted',
  'common.delete': 'Delete',
  'roles.table.emptyFilteredTitle': 'No matching roles',
  'roles.table.emptyFilteredHint': 'Adjust the search keyword',
  'roles.table.emptyTitle': 'No roles yet',
  'roles.table.emptyHint': 'Create a new role',
  'roles.table.currentPageReport': '{{total}} in total',
  'common.rowsPerPage': 'Items per page',
  'common.pageOf': 'Page {{page}} of {{total}}',
  'common.pagination.first': 'First page',
  'common.pagination.previous': 'Previous page',
  'common.pagination.next': 'Next page',
  'common.pagination.last': 'Last page',
};
//#endif
