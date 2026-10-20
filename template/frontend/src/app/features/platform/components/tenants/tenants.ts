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
import { lucidePlus, lucideRefreshCw, lucideSearch } from '@ng-icons/lucide';
import { toast } from '@spartan-ng/brain/sonner';
import { HlmButton } from '@spartan-ng/helm/button';
import {
  HlmInputGroup,
  HlmInputGroupAddon,
  HlmInputGroupInput,
} from '@spartan-ng/helm/input-group';
import { HlmTooltipImports } from '@spartan-ng/helm/tooltip';
import { PaginationState } from '@tanstack/angular-table';
import { combineLatest, defer, EMPTY, Subject } from 'rxjs';
import {
  catchError,
  debounceTime,
  distinctUntilChanged,
  finalize,
  startWith,
  switchMap,
} from 'rxjs/operators';

import { TenantDetailDialog } from './widgets/tenant-detail-dialog/tenant-detail-dialog';
import { TenantEditDialog } from './widgets/tenant-edit-dialog/tenant-edit-dialog';
import { TenantTable } from './widgets/tenant-table/tenant-table';
import { applicationErrorMessage } from '../../../../core/errors/application-http-error';
import { ConfirmService } from '../../../../core/feedback/confirm-service';
import { AuthorizationService } from '../../../../core/services/authorization-service';
//#if (Impersonation)
import { ImpersonationService } from '../../../../core/services/impersonation-service';
//#endif
import { LayoutService } from '../../../../core/services/layout-service';
import { PERMISSIONS } from '../../../../shared/constants/permission.constants';
//#if (!IncludeLocalization)
import { englishText } from '../../../../shared/utils/english-text';
//#endif
import { paginationFromQuery, tableStateToQuery } from '../../../../shared/utils/table-query-state';
import {
  CreateTenantInputDto,
  GetTenantsInputDto,
  TenantOutputDto,
  UpdateTenantInputDto,
} from '../../dtos/tenant.dto';
import { TenantService } from '../../services/tenant-service';

/** 租户管理页（仅宿主侧可见），列表状态（分页、关键字）落在 URL 查询参数上。 */
@Component({
  selector: 'app-tenants',
  imports: [
    NgIcon,
    HlmButton,
    HlmInputGroup,
    HlmInputGroupAddon,
    HlmInputGroupInput,
    ...HlmTooltipImports,
    TenantTable,
    TenantDetailDialog,
    TenantEditDialog,
    //#if (IncludeLocalization)
    TranslocoDirective,
    //#endif
  ],
  providers: [provideIcons({ lucidePlus, lucideRefreshCw, lucideSearch })],
  templateUrl: './tenants.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Tenants {
  private readonly tenantService = inject(TenantService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly confirmService = inject(ConfirmService);
  //#if (Impersonation)
  private readonly impersonationService = inject(ImpersonationService);
  //#endif
  private readonly authorizationService = inject(AuthorizationService);
  private readonly layoutService = inject(LayoutService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
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

  readonly tenants = signal<TenantOutputDto[]>([]);
  readonly totalCount = signal(0);
  readonly loading = signal(false);
  /** 列表加载失败且没有旧行可保留时的原因：有值时表格显示错误态与重试，不显示"暂无数据"。 */
  readonly loadError = signal<string | null>(null);

  // 列表状态全部来源于 URL 查询参数（刷新 / 前进后退 / 分享皆可复原）。
  readonly pagination = computed(() => paginationFromQuery(this.queryParams()));

  // 搜索框即时值：随 URL 回填，输入时乐观更新，防抖后写回 URL。
  readonly searchQuery = signal(this.route.snapshot.queryParamMap.get('keyword') ?? '');
  // 是否处于筛选/搜索态：用于区分「暂无数据」与「无匹配结果」的空状态。
  readonly hasActiveFilters = computed(() => this.searchQuery().trim().length > 0);

  readonly editDialogOpen = signal(false);
  readonly editingTenant = signal<TenantOutputDto | null>(null);

  readonly detailDialogOpen = signal(false);
  readonly detailTenant = signal<TenantOutputDto | null>(null);

  // 操作入口按权限裁剪；前端隐藏只影响体验，后端仍逐个请求校验。
  readonly canCreate = computed(() => this.authorizationService.has(PERMISSIONS.tenants.create));
  readonly canUpdate = computed(() => this.authorizationService.has(PERMISSIONS.tenants.update));
  readonly canDelete = computed(() => this.authorizationService.has(PERMISSIONS.tenants.delete));
  //#if (Impersonation)
  readonly canImpersonate = computed(() =>
    this.authorizationService.has(PERMISSIONS.tenants.impersonation),
  );
  //#endif

  constructor() {
    this.searchSubject
      .pipe(debounceTime(300), distinctUntilChanged(), takeUntilDestroyed(this.destroyRef))
      .subscribe((keyword) => this.updateQuery({ keyword: keyword.trim() || null, page: 1 }, true));

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
            return this.tenantService.getTenants(this.queryFromParams(params)).pipe(
              catchError((error: unknown) => {
                // 已有行时刷新失败：保留旧行，只做提示
                if (this.tenants().length > 0) {
                  toast.error(applicationErrorMessage(error));
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
      .subscribe((result) => {
        this.loadError.set(null);
        this.tenants.set([...result.items]);
        this.totalCount.set(result.totalCount);
      });

    // 面包屑末级文案由页面自行设置，与其他平台页保持同一约定。
    //#if (IncludeLocalization)
    const title = translateSignal('tenants.title', {}, { scope: 'tenants' });
    effect(() => this.layoutService.title.set(title()));
    //#else
    this.layoutService.title.set('Tenant management');
    //#endif
  }

  reload(): void {
    this.refreshRequests.next();
  }

  onSearchQueryChange(value: string): void {
    this.searchQuery.set(value);
    this.searchSubject.next(value);
  }

  onPaginationChange(pagination: PaginationState): void {
    this.updateQuery(tableStateToQuery(pagination, []));
  }

  openCreate(): void {
    this.editingTenant.set(null);
    this.editDialogOpen.set(true);
  }

  openEdit(tenant: TenantOutputDto): void {
    this.editingTenant.set(tenant);
    this.editDialogOpen.set(true);
  }

  openDetail(tenant: TenantOutputDto): void {
    this.detailTenant.set(tenant);
    this.detailDialogOpen.set(true);
  }

  /** 详情里点「编辑」：详情已自行关闭，这里只负责接力打开编辑。 */
  onDetailEdit(tenant: TenantOutputDto): void {
    this.openEdit(tenant);
  }

  onSave(payload: CreateTenantInputDto | UpdateTenantInputDto): void {
    const editing = this.editingTenant();
    const request$ = editing
      ? this.tenantService.updateTenant(editing.id, payload as UpdateTenantInputDto)
      : this.tenantService.createTenant(payload as CreateTenantInputDto);

    request$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => {
        this.editDialogOpen.set(false);
        this.reload();
        toast.success(this.savedMessage());
      },
      error: (error) => toast.error(applicationErrorMessage(error)),
    });
  }

  /** 启停切换：停用会阻止该租户用户登录，先确认再提交。 */
  async onToggleActive(tenant: TenantOutputDto): Promise<void> {
    const nextActive = !tenant.isActive;
    const confirmed = await this.confirmService.open({
      header: this.toggleTitle(nextActive),
      message: this.toggleDescription(tenant, nextActive),
      confirmText: this.toggleConfirmLabel(nextActive),
      variant: nextActive ? 'default' : 'destructive',
    });

    if (!confirmed) {
      return;
    }

    this.tenantService
      .setActivation(tenant.id, nextActive)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.reload();
          toast.success(this.toggledMessage(nextActive));
        },
        error: (error) => toast.error(applicationErrorMessage(error)),
      });
  }

  //#if (Impersonation)
  /**
   * 以该租户管理员身份登录。成功后由 ImpersonationService 整页跳转：会话被整体替换，
   * 本页状态全部作废。
   */
  async onImpersonate(tenant: TenantOutputDto): Promise<void> {
    const confirmed = await this.confirmService.open({
      header: this.impersonateTitle(),
      message: this.impersonateDescription(tenant),
      confirmText: this.impersonateTitle(),
      variant: 'default',
    });

    if (!confirmed) {
      return;
    }

    try {
      await this.impersonationService.start(tenant.id);
    } catch (error) {
      toast.error(applicationErrorMessage(error));
    }
  }

  private impersonateTitle(): string {
    //#if (IncludeLocalization)
    return this.transloco.translate('tenants.impersonate');
    //#else
    return 'Sign in as tenant';
    //#endif
  }

  private impersonateDescription(tenant: TenantOutputDto): string {
    //#if (IncludeLocalization)
    return this.transloco.translate('tenants.impersonateConfirm', {
      name: tenant.displayName || tenant.name,
    });
    //#else
    return `Sign in as the administrator of "${tenant.displayName || tenant.name}"? You will leave the host context until you exit.`;
    //#endif
  }

  //#endif
  async onDelete(tenant: TenantOutputDto): Promise<void> {
    const confirmed = await this.confirmService.open({
      header: this.deleteTitle(),
      message: this.deleteDescription(tenant),
      confirmText: this.deleteConfirmLabel(),
      variant: 'destructive',
    });

    if (!confirmed) {
      return;
    }

    this.tenantService
      .deleteTenant(tenant.id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.reload();
          toast.success(this.deletedMessage());
        },
        error: (error) => toast.error(applicationErrorMessage(error)),
      });
  }

  //#if (IncludeLocalization)
  private savedMessage = () => this.transloco.translate('tenants.saved');
  private deletedMessage = () => this.transloco.translate('tenants.deleted');
  private deleteTitle = () => this.transloco.translate('tenants.deleteTitle');
  private deleteDescription = (tenant: TenantOutputDto) =>
    this.transloco.translate('tenants.deleteDescription', {
      name: tenant.displayName || tenant.name,
    });
  private deleteConfirmLabel = () => this.transloco.translate('common.delete');
  private toggleTitle = (nextActive: boolean) =>
    this.transloco.translate(nextActive ? 'tenants.activateTitle' : 'tenants.deactivateTitle');
  private toggleDescription = (tenant: TenantOutputDto, nextActive: boolean) =>
    this.transloco.translate(
      nextActive ? 'tenants.activateDescription' : 'tenants.deactivateDescription',
      {
        name: tenant.displayName || tenant.name,
      },
    );
  private toggleConfirmLabel = (nextActive: boolean) =>
    this.transloco.translate(nextActive ? 'tenants.activate' : 'tenants.deactivate');
  private toggledMessage = (nextActive: boolean) =>
    this.transloco.translate(nextActive ? 'tenants.activated' : 'tenants.deactivated');
  //#else
  private savedMessage = () => 'Tenant saved';
  private deletedMessage = () => 'Tenant deleted';
  private deleteTitle = () => 'Delete tenant';
  private deleteDescription = (tenant: TenantOutputDto) =>
    `Delete "${tenant.displayName || tenant.name}"? Users of this tenant will no longer be able to sign in.`;
  private deleteConfirmLabel = () => 'Delete';
  private toggleTitle = (nextActive: boolean) =>
    nextActive ? 'Activate tenant' : 'Deactivate tenant';
  private toggleDescription = (tenant: TenantOutputDto, nextActive: boolean) =>
    nextActive
      ? `Activate "${tenant.displayName || tenant.name}"? Its users will be able to sign in again.`
      : `Deactivate "${tenant.displayName || tenant.name}"? Its users will not be able to sign in.`;
  private toggleConfirmLabel = (nextActive: boolean) => (nextActive ? 'Activate' : 'Deactivate');
  private toggledMessage = (nextActive: boolean) =>
    nextActive ? 'Tenant activated' : 'Tenant deactivated';
  //#endif

  private queryFromParams(params: ParamMap): GetTenantsInputDto {
    const pagination = paginationFromQuery(params);
    return {
      offset: pagination.pageIndex * pagination.pageSize,
      limit: pagination.pageSize,
      keyword: params.get('keyword') || undefined,
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
}
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'tenants.searchPlaceholder': 'Search tenants',
  'common.refresh': 'Refresh',
  'tenants.create': 'New tenant',
};
//#endif
