import { DOCUMENT } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  effect,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, ParamMap, Params, Router } from '@angular/router';
//#if (IncludeLocalization)
import { TranslocoDirective, TranslocoService, translateSignal } from '@jsverse/transloco';
//#endif
import { NgIcon, provideIcons } from '@ng-icons/core';
import {
  lucideBan,
  lucideBriefcase,
  lucideCircleCheck,
  lucideLockOpen,
  lucideMail,
  lucideMailCheck,
  lucidePlus,
  lucideRefreshCw,
  lucideSearch,
  lucideShieldCheck,
  lucideUser,
  lucideX,
} from '@ng-icons/lucide';
import { toast } from '@spartan-ng/brain/sonner';
import { HlmButton } from '@spartan-ng/helm/button';
import {
  HlmInputGroup,
  HlmInputGroupInput,
  HlmInputGroupAddon,
} from '@spartan-ng/helm/input-group';
import { HlmKbdImports } from '@spartan-ng/helm/kbd';
import { HlmSpinner } from '@spartan-ng/helm/spinner';
import { HlmTooltipImports } from '@spartan-ng/helm/tooltip';
import { PaginationState, RowSelectionState, SortingState } from '@tanstack/angular-table';
import { firstValueFrom, combineLatest, defer, EMPTY, Observable, Subject } from 'rxjs';
import {
  catchError,
  debounceTime,
  distinctUntilChanged,
  finalize,
  startWith,
  switchMap,
} from 'rxjs/operators';

//#if (LocalIdentity)
import {
  ReauthenticateDialog,
  ReauthenticationProof,
} from '../../../../core/components/reauthenticate-dialog/reauthenticate-dialog';
import { injectReauthentication } from '../../../../core/components/reauthenticate-dialog/reauthentication';
//#endif
import { applicationErrorMessage } from '../../../../core/errors/application-http-error';
import { ConfirmService } from '../../../../core/feedback/confirm-service';
import { AuthorizationService } from '../../../../core/services/authorization-service';
import { LayoutService } from '../../../../core/services/layout-service';
import { FacetedFilter } from '../../../../shared/components/faceted-filter/faceted-filter';
import { PERMISSIONS } from '../../../../shared/constants/permission.constants';
//#if (!IncludeLocalization)
import { englishText } from '../../../../shared/utils/english-text';
//#endif
import {
  paginationFromQuery,
  sortingFromQuery,
  tableStateToQuery,
  toApiSorting,
} from '../../../../shared/utils/table-query-state';
import { RoleBriefDto } from '../../dtos/role.dto';
// 裁掉这几个 DTO 之后剩余项能并成一行，而 prettier 会要求那样写；
// 条件块不能随形态换折行方式，因此在这里固定住
// prettier-ignore
import {
  BatchOperationOutputDto,
  //#if (LocalIdentity)
  CreateUserInputDto,
  //#endif
  GetUsersInputDto,
  //#if (LocalIdentity)
  ResetUserPasswordInputDto,
  //#endif
  //#if (LocalIdentity)
  UpdateUserInputDto,
  //#endif
  UserManagementOutputDto,
} from '../../dtos/user-management.dto';
import { RoleService } from '../../services/role-service';
import { UserManagementService } from '../../services/user-management-service';
import { BatchRolesDialog } from './widgets/batch-roles-dialog/batch-roles-dialog';
//#if (LocalIdentity)
import { ResetUserPasswordDialog } from './widgets/reset-user-password-dialog/reset-user-password-dialog';
//#endif
//#if (LocalIdentity)
import { UserEditDialog } from './widgets/user-edit-dialog/user-edit-dialog';
//#endif
import { UserRolesDialog } from './widgets/user-roles-dialog/user-roles-dialog';
import { UserTable } from './widgets/user-table/user-table';

//#if (LocalIdentity)
const USER_SORT_COLUMNS = ['username', 'email', 'lastLoginTime', 'creationTime'] as const;
//#else
const USER_SORT_COLUMNS = ['username', 'email', 'creationTime'] as const;
//#endif
const DEFAULT_USER_SORTING: SortingState = [{ id: 'creationTime', desc: true }];

@Component({
  selector: 'app-users',
  imports: [
    NgIcon,
    HlmButton,
    HlmSpinner,
    HlmInputGroup,
    HlmInputGroupInput,
    HlmInputGroupAddon,
    ...HlmKbdImports,
    ...HlmTooltipImports,
    FacetedFilter,
    //#if (IncludeLocalization)
    TranslocoDirective,
    //#endif
    UserTable,
    UserRolesDialog,
    BatchRolesDialog,
    //#if (LocalIdentity)
    UserEditDialog,
    //#endif
    //#if (LocalIdentity)
    ResetUserPasswordDialog,
    ReauthenticateDialog,
    //#endif
  ],
  providers: [
    provideIcons({
      lucidePlus,
      lucideRefreshCw,
      lucideSearch,
      lucideCircleCheck,
      lucideBan,
      lucideMailCheck,
      lucideMail,
      lucideShieldCheck,
      lucideBriefcase,
      lucideUser,
      lucideLockOpen,
      lucideX,
    }),
  ],
  // Esc 取消选择挂在 document 上：点空白处或 Safari 点按钮后焦点落在 body，挂宿主收不到
  host: { '(document:keydown.escape)': 'onEscape($event)' },
  templateUrl: './users.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Users {
  readonly resettingTwoFactorId = signal<string | null>(null);

  readonly activationId = signal<string | null>(null);
  readonly deletingId = signal<string | null>(null);
  readonly unlockingId = signal<string | null>(null);

  private readonly service = inject(UserManagementService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly document = inject(DOCUMENT);
  private readonly confirmService = inject(ConfirmService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly layoutService = inject(LayoutService);
  private readonly roleService = inject(RoleService);
  private readonly authorizationService = inject(AuthorizationService);
  //#if (IncludeLocalization)
  private readonly transloco = inject(TranslocoService);
  //#else
  protected readonly t = englishText(ENGLISH);
  //#endif

  private readonly searchSubject = new Subject<string>();
  private readonly refreshRequests = new Subject<void>();
  private readonly queryParams = toSignal(this.route.queryParamMap, {
    initialValue: this.route.snapshot.queryParamMap,
  });

  users = signal<UserManagementOutputDto[]>([]);
  totalRecords = signal(0);
  loading = signal(false);
  /** 列表加载失败且没有旧行可保留时的原因：有值时表格显示错误态与重试，不显示"暂无数据"。 */
  loadError = signal<string | null>(null);

  // 列表状态全部来源于 URL 查询参数（刷新 / 前进后退 / 分享皆可复原）。
  readonly pagination = computed(() => paginationFromQuery(this.queryParams()));
  readonly sorting = computed(() =>
    sortingFromQuery(this.queryParams(), USER_SORT_COLUMNS, DEFAULT_USER_SORTING),
  );

  editDialogVisible = signal(false);
  editDialogLoading = signal(false);
  editDialogSaving = signal(false);
  selectedUser = signal<UserManagementOutputDto | null>(null);
  //#if (LocalIdentity)
  resetPasswordDialogVisible = signal(false);
  resetPasswordSaving = signal(false);
  resettingUserId = signal<string | null>(null);
  /**
   * 改邮箱或标为已验证、重置口令、重置两步验证而管理员的会话不够新时，确认的是管理员本人：
   * 出示自己的口令或验证码，对话框按操作者本人的账号状态给出可选方式。
   */
  protected readonly reauth = injectReauthentication();
  /** 正在等确认身份的是哪个操作，决定对话框里的说明。 */
  protected readonly reauthAction = signal<'update' | 'resetPassword' | 'resetTwoFactor'>('update');
  //#endif

  // 搜索框即时值：随 URL 回填，输入时乐观更新，防抖后写回 URL。
  readonly searchQuery = signal(this.route.snapshot.queryParamMap.get('keyword') ?? '');
  // FacetedFilter 的取值由 URL 状态驱动。
  readonly selectedIsActive = computed(() => readBoolean(this.queryParams().get('isActive')));
  //#if (LocalIdentity)
  readonly selectedIsEmailVerified = computed(() =>
    readBoolean(this.queryParams().get('isEmailVerified')),
  );
  //#endif
  readonly selectedRoles = computed(() => this.queryParams().getAll('roles'));
  // 是否处于筛选/搜索态：用于区分「暂无数据」与「无匹配结果」的空状态。
  readonly hasActiveFilters = computed(() => {
    const commonFilters = this.searchQuery().trim().length > 0 || this.selectedIsActive() !== null;
    const authorizationFilters = this.selectedRoles().length > 0;
    //#if (LocalIdentity)
    return commonFilters || authorizationFilters || this.selectedIsEmailVerified() !== null;
    //#else
    return commonFilters || authorizationFilters;
    //#endif
  });
  //#if (IncludeLocalization)
  private readonly activeOptionsTexts = {
    active: translateSignal('users.status.active', {}, { scope: 'users' }),
    inactive: translateSignal('users.status.inactive', {}, { scope: 'users' }),
  };
  readonly activeOptions = computed(() => [
    {
      label: this.activeOptionsTexts.active(),
      value: true,
      icon: 'lucideCircleCheck',
    },
    { label: this.activeOptionsTexts.inactive(), value: false, icon: 'lucideBan' },
  ]);

  //#if (LocalIdentity)
  private readonly emailVerifiedOptionsTexts = {
    emailVerified: translateSignal('users.status.emailVerified', {}, { scope: 'users' }),
    emailUnverified: translateSignal('users.status.emailUnverified', {}, { scope: 'users' }),
  };
  readonly emailVerifiedOptions = computed(() => [
    {
      label: this.emailVerifiedOptionsTexts.emailVerified(),
      value: true,
      icon: 'lucideMailCheck',
    },
    {
      label: this.emailVerifiedOptionsTexts.emailUnverified(),
      value: false,
      icon: 'lucideMail',
    },
  ]);
  //#endif
  //#else
  readonly activeOptions = computed(() => [
    { label: 'Active', value: true, icon: 'lucideCircleCheck' },
    { label: 'Disabled', value: false, icon: 'lucideBan' },
  ]);

  //#if (LocalIdentity)
  readonly emailVerifiedOptions = computed(() => [
    { label: 'Email verified', value: true, icon: 'lucideMailCheck' },
    { label: 'Email not verified', value: false, icon: 'lucideMail' },
  ]);
  //#endif
  //#endif
  /** 角色筛选项来自角色 API，新建的角色立即出现在筛选器里。 */
  readonly availableRoles = signal<RoleBriefDto[]>([]);
  readonly roleOptions = computed(() =>
    this.availableRoles().map((role) => ({
      label: role.displayName,
      value: role.name,
      icon: 'lucideUser',
    })),
  );
  // 操作入口按权限裁剪。
  readonly canCreateUser = computed(() => this.authorizationService.has(PERMISSIONS.users.create));
  readonly canUpdateUser = computed(() => this.authorizationService.has(PERMISSIONS.users.update));
  readonly canDeleteUser = computed(() => this.authorizationService.has(PERMISSIONS.users.delete));
  readonly canManageUserRoles = computed(() =>
    this.authorizationService.has(PERMISSIONS.users.manageRoles),
  );
  /** 当前页勾选的用户（按 Id）。只在当前页内有效：翻页、筛选、排序或批量操作完成后清空。 */
  readonly rowSelection = signal<RowSelectionState>({});
  /** 批量操作的目标：只取当前显示的行里勾选的，不信任选择状态里残留的其他 Id。 */
  readonly selectedIds = computed(() => {
    const selection = this.rowSelection();
    return this.users()
      .map((user) => user.id)
      .filter((id) => selection[id]);
  });
  /** 有任一批量操作可用时才显示多选列。 */
  readonly canBatch = computed(() => this.canUpdateUser() || this.canManageUserRoles());
  readonly batchRolesVisible = signal(false);
  readonly batchBusy = signal(false);

  readonly rolesDialogVisible = signal(false);
  readonly rolesDialogUser = signal<UserManagementOutputDto | null>(null);

  openRolesDialog(user: UserManagementOutputDto): void {
    this.rolesDialogUser.set(user);
    this.rolesDialogVisible.set(true);
  }

  /** 角色变更会改变有效权限，保存后刷新列表与当前用户权限。 */
  onRolesSaved(): void {
    this.rolesDialogVisible.set(false);
    this.refreshRequests.next();
    this.authorizationService.reload().subscribe({ error: () => undefined });
  }

  constructor() {
    // 角色选项端点要求 ManageRoles；无该权限时不请求，避免制造必然 403 的噪声。
    if (this.authorizationService.has(PERMISSIONS.users.manageRoles)) {
      this.roleService.getOptions().subscribe({
        next: (roles) => this.availableRoles.set(roles),
        error: () => this.availableRoles.set([]),
      });
    }

    this.searchSubject
      .pipe(debounceTime(300), distinctUntilChanged(), takeUntilDestroyed(this.destroyRef))
      .subscribe((keyword) => this.updateQuery({ keyword: keyword.trim() || null, page: 1 }, true));

    // 选择只在当前页内有效：翻页、筛选、排序（都写在 URL 上）时清空。
    this.route.queryParamMap
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.rowSelection.set({}));

    // 搜索框即时值随 URL 回填（前进后退 / 分享链接场景）。
    effect(() => this.searchQuery.set(this.queryParams().get('keyword') ?? ''));

    // URL 变化或显式刷新时重新拉取列表。
    combineLatest([this.route.queryParamMap, this.refreshRequests.pipe(startWith(undefined))])
      .pipe(
        // 开始状态放进当前请求的订阅里：switchMap 先取消旧请求（其 finalize 置 false）再订阅新请求，
        // 写在 switchMap 外的话，旧请求的 finalize 会在新请求还没返回时把 loading 关掉。
        switchMap(([params]) =>
          defer(() => {
            this.loading.set(true);
            return this.service.getUsers(this.queryFromParams(params)).pipe(
              catchError((error: unknown) => {
                // 已有行时刷新失败：保留旧行，只做提示
                if (this.users().length > 0) {
                  this.showRequestError(error);
                } else {
                  this.loadError.set(applicationErrorMessage(error));
                }
                return EMPTY;
              }),
              finalize(() => this.loading.set(false)),
            );
          }),
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((data) => {
        this.loadError.set(null);
        this.users.set(data.items);
        // 刷新后已不在本页的行不再算作选中
        this.rowSelection.update((selection) =>
          Object.fromEntries(
            data.items.filter((user) => selection[user.id]).map((user) => [user.id, true]),
          ),
        );
        this.totalRecords.set(data.totalCount);
      });
    //#if (IncludeLocalization)

    const title = translateSignal('users.page.title', {}, { scope: 'users' });
    effect(() => this.layoutService.title.set(title()));
    //#else

    this.layoutService.title.set('User management');
    //#endif
  }

  onSearchQueryChange(value: string) {
    this.searchQuery.set(value);
    this.searchSubject.next(value);
  }

  onActiveChange(value: boolean | null | undefined) {
    this.updateQuery({ isActive: serializeBoolean(value), page: 1 });
  }

  //#if (LocalIdentity)
  onEmailVerifiedChange(value: boolean | null | undefined) {
    this.updateQuery({ isEmailVerified: serializeBoolean(value), page: 1 });
  }
  //#endif
  onRolesChange(values: string[]) {
    this.updateQuery({ roles: values.length ? values : null, page: 1 });
  }

  onPaginationChange(pagination: PaginationState) {
    this.updateQuery(tableStateToQuery(pagination, this.sorting()));
  }

  onSortingChange(sorting: SortingState) {
    this.updateQuery({
      ...tableStateToQuery({ ...this.pagination(), pageIndex: 0 }, sorting),
      page: 1,
    });
  }

  reloadList() {
    this.refreshRequests.next();
  }

  //#if (LocalIdentity)
  openAddDialog() {
    this.selectedUser.set(null);
    this.editDialogVisible.set(true);
  }

  //#endif
  //#if (LocalIdentity)
  openEditDialog(id: string) {
    this.selectedUser.set(null);
    this.editDialogVisible.set(true);
    this.editDialogLoading.set(true);

    this.service
      .getUser(id)
      .pipe(
        finalize(() => this.editDialogLoading.set(false)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: (user) => this.selectedUser.set(user),
        error: (error) => this.showRequestError(error),
      });
  }

  //#endif
  //#if (LocalIdentity)
  handleSave(data: CreateUserInputDto | UpdateUserInputDto, proof?: ReauthenticationProof) {
    this.editDialogSaving.set(true);
    const selected = this.selectedUser();
    const request = selected
      ? this.service.updateUser(selected.id, {
          ...(data as UpdateUserInputDto),
          password: proof?.password,
          twoFactorCode: proof?.twoFactorCode,
          passkey: proof?.passkey,
        })
      : this.service.createUser(data as CreateUserInputDto);

    request
      .pipe(
        finalize(() => this.editDialogSaving.set(false)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: () => {
          this.reauth.complete();
          //#if (IncludeLocalization)
          toast.success(this.transloco.translate('common.success'), {
            description: this.transloco.translate(
              selected ? 'users.toast.updated' : 'users.toast.created',
            ),
          });
          //#else
          toast.success('Success', {
            description: selected ? 'User updated successfully' : 'User created successfully',
          });
          //#endif
          this.editDialogVisible.set(false);
          this.reloadList();
        },
        // 改邮箱或标为已验证而管理员的会话不够新：先确认身份再重试
        error: (error) =>
          this.handleReauthenticationError('update', error, proof, (next) =>
            this.handleSave(data, next),
          ),
      });
  }

  //#endif
  async handleToggleActive(user: UserManagementOutputDto) {
    if (this.activationId() !== null) return;
    this.activationId.set(user.id);

    try {
      const confirmed = await this.confirmService.open({
        //#if (IncludeLocalization)
        message: this.transloco.translate(
          user.isActive ? 'users.confirm.disableMessage' : 'users.confirm.enableMessage',
          { name: user.username },
        ),
        header: this.transloco.translate(
          user.isActive ? 'users.confirm.disableHeader' : 'users.confirm.enableHeader',
        ),
        confirmText: this.transloco.translate('common.ok'),
        cancelText: this.transloco.translate('common.cancel'),
        //#else
        message: user.isActive
          ? `Are you sure you want to disable user ${user.username}?`
          : `Are you sure you want to enable user ${user.username}?`,
        header: user.isActive ? 'Confirm disable' : 'Confirm enable',
        //#endif
      });
      if (!confirmed) {
        return;
      }

      const request = user.isActive
        ? this.service.disableUser(user.id)
        : this.service.enableUser(user.id);

      try {
        await firstValueFrom(request);

        //#if (IncludeLocalization)
        toast.success(this.transloco.translate('common.success'), {
          description: this.transloco.translate(
            user.isActive ? 'users.toast.disabled' : 'users.toast.enabled',
          ),
        });
        //#else
        toast.success('Success', {
          description: user.isActive ? 'User disabled' : 'User enabled',
        });
        //#endif
        this.reloadList();
      } catch (error: unknown) {
        this.showRequestError(error);
      }
    } finally {
      this.activationId.set(null);
    }
  }

  //#if (LocalIdentity)
  async handleDelete(user: UserManagementOutputDto) {
    if (this.deletingId() !== null) return;
    this.deletingId.set(user.id);

    try {
      if (user.isSuperAdmin) {
        //#if (IncludeLocalization)
        toast.warning(this.transloco.translate('users.toast.cannotDeleteSummary'), {
          description: this.transloco.translate('users.toast.cannotDeleteSuperAdmin'),
        });
        //#else
        toast.warning('Cannot delete', {
          description: 'The built-in super administrator cannot be deleted',
        });
        //#endif
        return;
      }

      const confirmed = await this.confirmService.open({
        //#if (IncludeLocalization)
        message: this.transloco.translate('users.confirm.deleteMessage', { name: user.username }),
        header: this.transloco.translate('users.confirm.deleteHeader'),
        confirmText: this.transloco.translate('common.ok'),
        cancelText: this.transloco.translate('common.cancel'),
        //#else
        message: `Are you sure you want to delete user ${user.username}? Once deleted, the user will no longer be able to sign in.`,
        header: 'Confirm delete',
        //#endif
        variant: 'destructive',
      });
      if (!confirmed) {
        return;
      }

      try {
        await firstValueFrom(this.service.deleteUser(user.id));

        //#if (IncludeLocalization)
        toast.success(this.transloco.translate('common.success'), {
          description: this.transloco.translate('users.toast.deleted'),
        });
        //#else
        toast.success('Success', { description: 'User deleted' });
        //#endif
        this.reloadList();
      } catch (error: unknown) {
        this.showRequestError(error);
      }
    } finally {
      this.deletingId.set(null);
    }
  }
  //#endif
  //#if (LocalIdentity)
  async handleUnlock(user: UserManagementOutputDto) {
    if (this.unlockingId() !== null) return;
    this.unlockingId.set(user.id);

    try {
      const confirmed = await this.confirmService.open({
        //#if (IncludeLocalization)
        message: this.transloco.translate('users.confirm.unlockMessage', { name: user.username }),
        header: this.transloco.translate('users.confirm.unlockHeader'),
        confirmText: this.transloco.translate('common.ok'),
        cancelText: this.transloco.translate('common.cancel'),
        //#else
        message: `Unlock user ${user.username}? The failed sign-in count will also be reset.`,
        header: 'Confirm unlock',
        //#endif
      });
      if (!confirmed) {
        return;
      }

      try {
        await firstValueFrom(this.service.unlockUser(user.id));

        //#if (IncludeLocalization)
        toast.success(this.transloco.translate('common.success'), {
          description: this.transloco.translate('users.toast.unlocked'),
        });
        //#else
        toast.success('Success', { description: 'User unlocked' });
        //#endif
        this.reloadList();
      } catch (error: unknown) {
        this.showRequestError(error);
      }
    } finally {
      this.unlockingId.set(null);
    }
  }

  protected onReauthenticationVisibleChange(visible: boolean): void {
    const saving = this.reauth.saving();
    this.reauth.setVisible(visible);
    if (!visible && !saving) this.resettingTwoFactorId.set(null);
  }
  async handleResetTwoFactor(user: UserManagementOutputDto) {
    if (this.resettingTwoFactorId() !== null) return;
    this.resettingTwoFactorId.set(user.id);
    let submitted = false;
    try {
      const confirmed = await this.confirmService.open({
        //#if (IncludeLocalization)
        message: this.transloco.translate('users.confirm.resetTwoFactorMessage', {
          name: user.username,
        }),
        header: this.transloco.translate('users.confirm.resetTwoFactorHeader'),
        confirmText: this.transloco.translate('common.ok'),
        cancelText: this.transloco.translate('common.cancel'),
        //#else
        message: `Reset two-factor authentication for ${user.username}? They will be signed out everywhere and can sign in with just their password.`,
        header: 'Confirm reset',
        //#endif
        variant: 'destructive',
      });
      if (!confirmed) {
        return;
      }

      this.resetTwoFactor(user);
      submitted = true;
    } finally {
      if (!submitted) this.resettingTwoFactorId.set(null);
    }
  }

  /**
   * 会话不够新时记下重试动作并请管理员确认身份；凭证被拒时留在对话框里就地说明。其余错误照常提示。
   */
  private handleReauthenticationError(
    action: 'update' | 'resetPassword' | 'resetTwoFactor',
    error: unknown,
    proof: ReauthenticationProof | undefined,
    retry: (proof: ReauthenticationProof) => void,
  ) {
    if (proof === undefined) {
      this.reauthAction.set(action);
    }
    if (!this.reauth.handleError(error, proof, retry)) {
      this.showRequestError(error);
    }
  }

  private resetTwoFactor(user: UserManagementOutputDto, proof?: ReauthenticationProof) {
    this.service
      .resetTwoFactor(user.id, {
        password: proof?.password,
        twoFactorCode: proof?.twoFactorCode,
        passkey: proof?.passkey,
      })
      .pipe(
        finalize(() => {
          if (!this.reauth.visible()) this.resettingTwoFactorId.set(null);
        }),
      )
      .subscribe({
        next: () => {
          this.reauth.complete();
          //#if (IncludeLocalization)
          toast.success(this.transloco.translate('common.success'), {
            description: this.transloco.translate('users.toast.twoFactorReset'),
          });
          //#else
          toast.success('Success', { description: 'Two-factor authentication reset' });
          //#endif
          this.reloadList();
        },
        error: (error) =>
          this.handleReauthenticationError('resetTwoFactor', error, proof, (next) =>
            this.resetTwoFactor(user, next),
          ),
      });
  }

  openResetPasswordDialog(id: string) {
    this.resettingUserId.set(id);
    this.resetPasswordDialogVisible.set(true);
  }

  handleResetPassword(data: ResetUserPasswordInputDto, proof?: ReauthenticationProof) {
    const id = this.resettingUserId();
    if (!id) {
      return;
    }

    this.resetPasswordSaving.set(true);
    this.service
      .resetPassword(id, {
        ...data,
        currentPassword: proof?.password,
        twoFactorCode: proof?.twoFactorCode,
        passkey: proof?.passkey,
      })
      .pipe(
        finalize(() => this.resetPasswordSaving.set(false)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: () => {
          this.reauth.complete();
          //#if (IncludeLocalization)
          toast.success(this.transloco.translate('common.success'), {
            description: this.transloco.translate('users.toast.passwordReset'),
          });
          //#else
          toast.success('Success', { description: 'Password has been reset' });
          //#endif
          this.resetPasswordDialogVisible.set(false);
          this.resettingUserId.set(null);
        },
        // 重置口令总要管理员的会话刚认证过：不够新时先确认身份再重试
        error: (error) =>
          this.handleReauthenticationError('resetPassword', error, proof, (next) =>
            this.handleResetPassword(data, next),
          ),
      });
  }

  //#endif
  private queryFromParams(params: ParamMap): GetUsersInputDto {
    const pagination = paginationFromQuery(params);
    const roles = params.getAll('roles');
    return {
      offset: pagination.pageIndex * pagination.pageSize,
      limit: pagination.pageSize,
      keyword: params.get('keyword') || undefined,
      isActive: readBoolean(params.get('isActive')) ?? undefined,
      //#if (LocalIdentity)
      isEmailVerified: readBoolean(params.get('isEmailVerified')) ?? undefined,
      //#endif
      roles: roles.length ? roles : undefined,
      // prettier-ignore
      sorting: toApiSorting(
        sortingFromQuery(params, USER_SORT_COLUMNS, DEFAULT_USER_SORTING)
          //#if (LocalIdentity)
          .map((item) => ({ ...item, id: item.id === 'lastLoginTime' ? 'lastLogin.Time' : item.id }))
          //#endif
      ),
    };
  }

  private updateQuery(queryParams: Params, replaceUrl = false): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams,
      queryParamsHandling: 'merge',
      replaceUrl,
    });
  }

  clearSelection(): void {
    this.rowSelection.set({});
  }

  /**
   * 对话框、菜单已处理的 Esc 会 preventDefault；鼠标打开的菜单焦点仍在触发按钮上，Esc 不经菜单，
   * 所以浮层里还开着菜单、对话框或下拉列表时也不清。焦点在输入框里时 Esc 留给输入框本身。
   */
  protected onEscape(event: Event): void {
    const target = event.target;
    const inField =
      target instanceof HTMLElement &&
      !!target.closest('input, textarea, select, [contenteditable="true"]');
    const overlayOpen = !!this.document.querySelector(
      '.cdk-overlay-container :is([role="menu"], [role="dialog"], [role="alertdialog"], [role="listbox"])',
    );
    if (!event.defaultPrevented && !inField && !overlayOpen) {
      this.clearSelection();
    }
  }

  async batchSetActive(active: boolean): Promise<void> {
    if (this.batchBusy()) return;
    this.batchBusy.set(true);
    let submitted = false;
    try {
      const count = this.selectedIds().length;
      const confirmed = await this.confirmService.open({
        message: this.text(active ? 'users.batch.enableMessage' : 'users.batch.disableMessage', {
          count,
        }),
        header: this.text(active ? 'users.batch.enableHeader' : 'users.batch.disableHeader'),
        //#if (IncludeLocalization)
        confirmText: this.text('common.ok'),
        cancelText: this.text('common.cancel'),
        //#endif
        variant: active ? undefined : 'destructive',
      });
      if (confirmed) {
        const ids = this.selectedIds();
        submitted = true;
        this.runBatch(
          active ? this.service.batchEnableUsers({ ids }) : this.service.batchDisableUsers({ ids }),
        );
      }
    } finally {
      if (!submitted) this.batchBusy.set(false);
    }
  }
  //#if (LocalIdentity)

  async batchUnlock(): Promise<void> {
    if (this.batchBusy()) return;
    this.batchBusy.set(true);
    let submitted = false;
    try {
      const confirmed = await this.confirmService.open({
        message: this.text('users.batch.unlockMessage', { count: this.selectedIds().length }),
        header: this.text('users.batch.unlockHeader'),
        //#if (IncludeLocalization)
        confirmText: this.text('common.ok'),
        cancelText: this.text('common.cancel'),
        //#endif
      });
      if (confirmed) {
        submitted = true;
        this.runBatch(this.service.batchUnlockUsers({ ids: this.selectedIds() }));
      }
    } finally {
      if (!submitted) this.batchBusy.set(false);
    }
  }
  //#endif

  batchAssignRoles(roleIds: string[]): void {
    if (this.batchBusy()) return;
    this.runBatch(
      this.service
        .batchAssignUserRoles({ ids: this.selectedIds(), roleIds })
        .pipe(takeUntilDestroyed(this.destroyRef)),
      () => {
        this.batchRolesVisible.set(false);
        // 角色变化会改变有效权限，选中的可能包括自己
        this.authorizationService.reload().subscribe({ error: () => undefined });
      },
    );
  }

  /**
   * 执行一次批量操作并汇总逐条结果：全部成功给成功提示；有失败时列出失败的用户与原因（说明来自
   * 服务端，已本地化）。完成后清空选择并刷新当前页；整批被拒（如角色不存在）时保留选择以便重试。
   */
  private runBatch(request: Observable<BatchOperationOutputDto>, onDone?: () => void): void {
    this.batchBusy.set(true);
    request.pipe(finalize(() => this.batchBusy.set(false))).subscribe({
      next: (result) => {
        this.reportBatch(result);
        this.rowSelection.set({});
        onDone?.();
        this.reloadList();
      },
      error: (error) => this.showRequestError(error),
    });
  }

  private reportBatch(result: BatchOperationOutputDto): void {
    const failed = result.items.filter((item) => !item.succeeded);
    const succeeded = result.items.length - failed.length;
    if (failed.length === 0) {
      toast.success(this.text('common.success'), {
        description: this.text('users.batch.allSucceeded', { count: succeeded }),
      });
      return;
    }

    const names = new Map(this.users().map((user) => [user.id, user.username]));
    const reasons = failed.map(
      (item) => `${names.get(item.id) ?? item.id}: ${item.message ?? item.errorCode ?? ''}`,
    );
    toast.warning(this.text('users.batch.partial', { succeeded, failed: failed.length }), {
      description: reasons.join('\n'),
      duration: 10_000,
    });
  }

  /** 事件发生时的一次性文案；两种形态共用一个入口，调用处不必各写一遍条件分支。 */
  private text(key: string, params?: Record<string, unknown>): string {
    //#if (IncludeLocalization)
    return this.transloco.translate(key, params);
    //#else
    return this.t(key, params);
    //#endif
  }

  private showRequestError(error: unknown): void {
    //#if (IncludeLocalization)
    toast.error(this.transloco.translate('common.requestError'), {
      description: applicationErrorMessage(error),
    });
    //#else
    toast.error('Request failed', { description: applicationErrorMessage(error) });
    //#endif
  }
}

function readBoolean(value: string | null): boolean | null {
  return value === 'true' ? true : value === 'false' ? false : null;
}

function serializeBoolean(value: boolean | null | undefined): string | null {
  return value === true ? 'true' : value === false ? 'false' : null;
}
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'users.filter.searchPlaceholder': 'Search username / email / display name...',
  'users.table.colStatus': 'Status',
  'common.clearFilter': 'Clear filter',
  'common.noResults': 'No results',
  'users.filter.emailLabel': 'Email status',
  'users.table.colRole': 'Role',
  'common.refresh': 'Refresh',
  'users.actions.newUser': 'New user',
  'users.batch.toolbar': 'Bulk actions',
  'users.batch.selected': '{{count}} selected',
  'users.batch.enable': 'Enable',
  'users.batch.disable': 'Disable',
  'users.batch.unlock': 'Unlock',
  'users.batch.assignRoles': 'Add roles',
  'users.batch.clear': 'Clear selection',
  'users.batch.enableHeader': 'Enable users',
  'users.batch.enableMessage': 'Enable the {{count}} selected users?',
  'users.batch.disableHeader': 'Disable users',
  'users.batch.disableMessage':
    'Disable the {{count}} selected users? They will be signed out and cannot sign in.',
  'users.batch.unlockHeader': 'Unlock users',
  'users.batch.unlockMessage':
    'Unlock the {{count}} selected users? Their failed sign-in counts are reset too.',
  'users.batch.allSucceeded': 'Done for {{count}} users.',
  'users.batch.partial': '{{succeeded}} succeeded, {{failed}} failed',
  'common.success': 'Success',
  //#if (LocalIdentity)
  'users.reauth.update':
    "Changing someone's sign-in email or its verified status needs you to confirm it is you.",
  'users.reauth.resetPassword': "Resetting someone's password needs you to confirm it is you.",
  'users.reauth.resetTwoFactor':
    "Resetting someone's two-factor authentication needs you to confirm it is you.",
  //#endif
};
//#endif
