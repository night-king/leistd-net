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
  lucideBan,
  lucideChevronRight,
  lucideCircleAlert,
  lucideCircleCheck,
  lucideEllipsis,
  lucideKey,
  lucideLockOpen,
  lucideShieldCheck,
  lucideShieldOff,
  //#if (LocalIdentity)
  lucidePencil,
  //#endif
  lucideSearchX,
  lucideSortAsc,
  lucideSortDesc,
  lucideTrash2,
  lucideUsers,
} from '@ng-icons/lucide';
import { HlmAvatarImports } from '@spartan-ng/helm/avatar';
import { HlmBadge } from '@spartan-ng/helm/badge';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmCheckbox } from '@spartan-ng/helm/checkbox';
import { HlmDropdownMenuImports } from '@spartan-ng/helm/dropdown-menu';
import { HlmSpinner } from '@spartan-ng/helm/spinner';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { HlmTooltipImports } from '@spartan-ng/helm/tooltip';
import {
  ColumnDef,
  PaginationState,
  RowSelectionState,
  SortingState,
} from '@tanstack/angular-table';

import { AccountPreferencesService } from '../../../../../../core/services/account-preferences-service';
import { AuthService } from '../../../../../../core/services/auth-service';
import {
  type ReadonlyListItem,
  ReadonlyListPopover,
} from '../../../../../../shared/components/readonly-list-popover/readonly-list-popover';
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
import {
  tableSortAria,
  type TableSortAria,
  tableSortIcon,
  toggleTableSort,
} from '../../../../../../shared/utils/table-sorting';
import { tableViewportSignal } from '../../../../../../shared/utils/table-viewport';
import { RoleBriefDto } from '../../../../dtos/role.dto';
import { UserManagementOutputDto } from '../../../../dtos/user-management.dto';

type BadgeVariant = 'default' | 'secondary' | 'destructive' | 'outline';

@Component({
  selector: 'app-user-table',
  imports: [
    NgTemplateOutlet,
    ColumnValue,
    ReadonlyListPopover,
    StatusDot,
    TableFit,
    AppDate,
    AppNumber,
    NgIcon,
    HlmBadge,
    HlmButton,
    HlmCheckbox,
    HlmSpinner,
    TablePaginator,
    ...HlmAvatarImports,
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
      lucideBan,
      lucideChevronRight,
      lucideCircleAlert,
      lucideCircleCheck,
      lucideEllipsis,
      lucideKey,
      lucideLockOpen,
      lucideShieldCheck,
      lucideShieldOff,
      //#if (LocalIdentity)
      lucidePencil,
      //#endif
      lucideSearchX,
      lucideSortAsc,
      lucideSortDesc,
      lucideTrash2,
      lucideUsers,
    }),
  ],
  templateUrl: './user-table.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class UserTable {
  private readonly authService = inject(AuthService);
  // 统一按展示时区渲染，避免同一时刻在不同页面显示成不同时间。
  protected readonly displayTimeZone = inject(AccountPreferencesService).timeZone;
  protected readonly displayLocale = inject(AccountPreferencesService).displayLocale;
  //#if (!IncludeLocalization)
  protected readonly t = englishText(ENGLISH);
  //#endif

  readonly deletingId = input<string | null>(null);
  readonly activationId = input<string | null>(null);
  readonly unlockingId = input<string | null>(null);
  readonly resettingTwoFactorId = input<string | null>(null);
  readonly users = input<UserManagementOutputDto[]>([]);
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
  readonly canManageRoles = input(false);

  /** 只出现在溢出菜单里的操作；分配角色（及本地身份下的编辑）在桌面端是行内按钮。 */
  readonly hasMenuOnlyActions = computed(() => this.canUpdate() || this.canDelete());
  /** 一个可用操作都没有时不渲染溢出菜单，避免留下点开即空的按钮。 */
  readonly hasRowActions = computed(() => this.hasMenuOnlyActions() || this.canManageRoles());

  /**
   * 是否显示多选列：有批量操作可用时由页面打开。选择状态归页面所有（翻页、筛选、排序时由页面清空），
   * 表格只回报勾选变化；选择只在当前页内有效。
   */
  readonly selectable = input(false);
  readonly rowSelection = input<RowSelectionState>({});
  readonly rowSelectionChange = output<RowSelectionState>();

  readonly paginationChange = output<PaginationState>();
  readonly sortingChange = output<SortingState>();
  //#if (LocalIdentity)
  readonly edit = output<string>();
  //#endif
  readonly toggleActive = output<UserManagementOutputDto>();
  //#if (LocalIdentity)
  readonly resetPassword = output<string>();
  readonly unlock = output<UserManagementOutputDto>();
  readonly resetTwoFactor = output<UserManagementOutputDto>();
  //#endif
  readonly delete = output<UserManagementOutputDto>();
  /** 角色分配是独立命令，与资料编辑分开触发。 */
  readonly manageRoles = output<UserManagementOutputDto>();

  protected readonly tableViewport = tableViewportSignal();
  /** 主列内容外层：最窄一档下长名称截断，不撑宽表格（见 TITLE_COLUMN_META）。 */
  protected readonly titleContentClass = TITLE_CONTENT_CLASS;
  private readonly tableFit = viewChild(TableFit);
  /** 实际折叠档位：视口给上限，容器放不下再降一档（见 TableFit，与容器上的 data-table-fit 同值）。 */
  protected readonly foldLevel = computed(() => this.tableFit()?.level() ?? this.tableViewport());

  protected readonly columns: ColumnDef<AppTableFeatures, UserManagementOutputDto>[] = [
    {
      id: 'select',
      enableSorting: false,
      enableHiding: false,
      meta: { priority: 'primary', locked: true, headClass: 'w-10 ps-4', cellClass: 'w-10 ps-4' },
    },
    {
      accessorKey: 'username',
      id: 'username',
      enableHiding: false,
      meta: TITLE_COLUMN_META,
    },
    // 用户列已有显示名与用户名可供识别，邮箱先让位；平板宽度留着"最近登录"，便于发现长期不用的账号
    { accessorKey: 'email', id: 'email', meta: { priority: 'tertiary', detail: true } },
    {
      accessorKey: 'roles',
      id: 'roles',
      enableSorting: false,
      meta: { priority: 'secondary', detail: true },
    },
    {
      accessorKey: 'isActive',
      id: 'status',
      enableSorting: false,
      enableHiding: false,
      meta: { priority: 'primary', locked: true },
    },
    //#if (LocalIdentity)
    {
      accessorKey: 'lastLoginTime',
      id: 'lastLoginTime',
      meta: { priority: 'secondary', detail: true },
    },
    //#endif
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

  private readonly columnVisibility = computed(() => ({
    ...tableColumnVisibility(this.columns, this.foldLevel()),
    select: this.selectable(),
  }));

  /** 列标签的词条键：表头与展开详情共用，切换语言时两处一起更新。 */
  protected readonly columnLabelKeys: Readonly<Record<string, string>> = {
    username: 'users.table.colUser',
    email: 'users.table.colEmail',
    roles: 'users.table.colRole',
    status: 'users.table.colStatus',
    //#if (LocalIdentity)
    lastLoginTime: 'users.table.colLastLogin',
    //#endif
    creationTime: 'users.table.colCreatedAt',
  };

  readonly hasCollapsedColumns = computed(() => this.foldLevel() !== 'desktop');

  /** 展开详情里列出的列：被折叠的数据列，按列定义顺序。 */
  protected detailColumns() {
    return hiddenDetailColumns(this.table);
  }

  protected readonly table = injectAppTable(() => ({
    data: this.users(),
    columns: this.columns,
    getRowId: (row) => row.id,
    manualPagination: true,
    manualSorting: true,
    rowCount: this.totalCount(),
    // 加载中表格里还是上一页的行，此时勾选的会是看不见的旧数据
    enableRowSelection: !this.loading(),
    onPaginationChange: (updater) =>
      this.paginationChange.emit(resolveTableUpdater(updater, this.pagination())),
    onSortingChange: (updater) =>
      this.sortingChange.emit(resolveTableUpdater(updater, this.sorting())),
    onRowSelectionChange: (updater) =>
      this.rowSelectionChange.emit(resolveTableUpdater(updater, this.rowSelection())),
    state: {
      columnVisibility: this.columnVisibility(),
      rowSelection: this.rowSelection(),
      pagination: this.pagination(),
      sorting: this.sorting(),
    },
  }));

  readonly currentPage = computed(() => this.pagination().pageIndex + 1);
  readonly totalPages = computed(() => Math.max(1, this.table.getPageCount()));

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

  getVisibleRoles(user: UserManagementOutputDto): RoleBriefDto[] {
    return (user.roles ?? []).slice(0, 2);
  }

  getHiddenRoles(user: UserManagementOutputDto): RoleBriefDto[] {
    return (user.roles ?? []).slice(2);
  }

  /** 面板列出全部角色：显示名为主，稳定名称与之不同时附在下面。 */
  getRoleListItems(user: UserManagementOutputDto): ReadonlyListItem[] {
    return (user.roles ?? []).map((role) => ({
      id: role.id,
      label: role.displayName,
      detail: role.name,
    }));
  }

  isSuperAdmin(user: UserManagementOutputDto): boolean {
    return user.isSuperAdmin;
  }

  /** 当前登录者本人：不能停用或删除自己（服务端同样拒绝）。 */
  isSelf(user: UserManagementOutputDto): boolean {
    return user.id === this.authService.currentUser()?.id;
  }

  isOtherSuperAdmin(user: UserManagementOutputDto): boolean {
    return user.isSuperAdmin && user.id !== this.authService.currentUser()?.id;
  }

  /** 他人查看内置超管行时，菜单内原因说明的元素 id，供被禁用的菜单项 aria-describedby 引用；其他行为 null。 */
  superAdminReasonId(user: UserManagementOutputDto): string | null {
    return this.isOtherSuperAdmin(user) ? `user-super-admin-${user.id}` : null;
  }

  /** 角色徽章样式：角色由管理员自由创建，统一用中性样式，只按是否默认角色弱区分。 */
  getRoleVariant(): BadgeVariant {
    return 'outline';
  }
}
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'common.loadFailed': "Couldn't load the list",
  'common.retry': 'Retry',
  'users.table.colUser': 'User',
  'users.table.colEmail': 'Email',
  'users.table.colRole': 'Role',
  'users.table.colStatus': 'Status',
  'users.table.colLastLogin': 'Last sign-in',
  'users.table.colCreatedAt': 'Created at',
  'common.actions': 'Actions',
  'users.batch.selectAll': 'Select all users on this page',
  'users.batch.selectRow': 'Select {{name}}',
  'common.details': 'View details',
  'users.popover.rolesTitle': 'Roles',
  'users.popover.rolesTrigger': 'View all roles of {{name}} ({{count}})',
  'users.popover.filterRoles': 'Filter roles',
  'users.popover.noMatchingRoles': 'No matching roles',
  'users.status.active': 'Active',
  'users.status.inactive': 'Disabled',
  'users.status.lockedUntil': 'Locked until {{time}}',
  'users.status.lockedIndefinitely': 'Locked until an administrator unlocks it',
  'users.status.locked': 'Locked',
  'users.status.emailUnverified': 'Email not verified',
  'users.filter.emailLabel': 'Email status',
  'common.edit': 'Edit',
  'users.actions.manageRoles': 'Assign roles',
  'users.tooltip.resetPassword': 'Reset password',
  'users.tooltip.unlock': 'Unlock',
  'users.tooltip.resetTwoFactor': 'Reset two-factor authentication',
  'users.tooltip.disable': 'Disable',
  'users.tooltip.enable': 'Enable',
  'users.tooltip.selfNoDisable': "You can't disable your own account",
  'users.tooltip.selfNoResetTwoFactor': 'Manage your own in Personal settings',
  'users.tooltip.selfNoDelete': "You can't delete your own account",
  'users.tooltip.superAdminNoManage':
    'Only the built-in super administrator can do this to their own account',
  'common.delete': 'Delete',
  'users.table.emptyFilteredTitle': 'No matching users',
  'users.table.emptyFilteredHint': 'Adjust the search or filters',
  'users.table.emptyTitle': 'No users yet',
  'users.table.emptyHint': 'Create a new user',
  'users.table.currentPageReport': '{{total}} in total',
  'common.rowsPerPage': 'Items per page',
  'common.pageOf': 'Page {{page}} of {{total}}',
  'common.pagination.first': 'First page',
  'common.pagination.previous': 'Previous page',
  'common.pagination.next': 'Next page',
  'common.pagination.last': 'Last page',
};
//#endif
