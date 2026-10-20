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
  lucideGlobe,
  lucideLockKeyhole,
  lucideMonitor,
  lucidePlus,
  lucideRefreshCw,
  lucideSearch,
  lucideServer,
  lucideUnlock,
} from '@ng-icons/lucide';
import { toast } from '@spartan-ng/brain/sonner';
import { HlmButton } from '@spartan-ng/helm/button';
import {
  HlmInputGroup,
  HlmInputGroupInput,
  HlmInputGroupAddon,
} from '@spartan-ng/helm/input-group';
import { HlmTooltipImports } from '@spartan-ng/helm/tooltip';
import { PaginationState, SortingState } from '@tanstack/angular-table';
import { combineLatest, defer, EMPTY, of, Subject } from 'rxjs';
import {
  catchError,
  debounceTime,
  distinctUntilChanged,
  finalize,
  startWith,
  switchMap,
} from 'rxjs/operators';

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
import {
  CreateOpenApplicationInputDto,
  GetOpenApplicationsInputDto,
  OpenApplicationClientType,
  OpenApplicationOutputDto,
  OpenApplicationType,
  UpdateOpenApplicationInputDto,
} from '../../dtos/open-application.dto';
import { OpenApplicationService } from '../../services/open-application-service';
import { OpenApplicationEditDialog } from './widgets/open-application-edit-dialog/open-application-edit-dialog';
import { OpenApplicationTable } from './widgets/open-application-table/open-application-table';
import {
  SecretRevealDialog,
  SecretRevealKind,
} from './widgets/secret-reveal-dialog/secret-reveal-dialog';

const APPLICATION_SORT_COLUMNS = ['clientId', 'creationTime'] as const;
const DEFAULT_APPLICATION_SORTING: SortingState = [{ id: 'creationTime', desc: true }];

@Component({
  selector: 'app-open-applications',
  imports: [
    NgIcon,
    HlmButton,
    HlmInputGroup,
    HlmInputGroupInput,
    HlmInputGroupAddon,
    ...HlmTooltipImports,
    FacetedFilter,
    //#if (IncludeLocalization)
    TranslocoDirective,
    //#endif
    OpenApplicationTable,
    OpenApplicationEditDialog,
    SecretRevealDialog,
  ],
  providers: [
    provideIcons({
      lucidePlus,
      lucideRefreshCw,
      lucideSearch,
      lucideGlobe,
      lucideMonitor,
      lucideServer,
      lucideUnlock,
      lucideLockKeyhole,
    }),
  ],
  templateUrl: './open-applications.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class OpenApplications {
  private readonly service = inject(OpenApplicationService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly confirmService = inject(ConfirmService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly layoutService = inject(LayoutService);
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

  applications = signal<OpenApplicationOutputDto[]>([]);
  totalRecords = signal(0);
  loading = signal(false);
  /** 列表加载失败且没有旧行可保留时的原因：有值时表格显示错误态与重试，不显示"暂无数据"。 */
  loadError = signal<string | null>(null);

  // 列表状态全部来源于 URL 查询参数（刷新 / 前进后退 / 分享皆可复原）。
  readonly pagination = computed(() => paginationFromQuery(this.queryParams()));
  readonly sorting = computed(() =>
    sortingFromQuery(this.queryParams(), APPLICATION_SORT_COLUMNS, DEFAULT_APPLICATION_SORTING),
  );

  // 操作入口按权限裁剪；前端隐藏只影响体验，后端仍逐个请求校验。
  readonly canCreate = computed(() =>
    this.authorizationService.has(PERMISSIONS.openApplications.create),
  );
  readonly canUpdate = computed(() =>
    this.authorizationService.has(PERMISSIONS.openApplications.update),
  );
  readonly canDelete = computed(() =>
    this.authorizationService.has(PERMISSIONS.openApplications.delete),
  );
  readonly canResetSecret = computed(() =>
    this.authorizationService.has(PERMISSIONS.openApplications.resetSecret),
  );

  editDialogVisible = signal(false);
  editDialogLoading = signal(false);
  editDialogSaving = signal(false);
  selectedApplication = signal<OpenApplicationOutputDto | null>(null);
  // 可授予的 scope：取不到时编辑框里只是没有 scope 选项，列表照常可用
  readonly scopes = toSignal(this.service.getScopes().pipe(catchError(() => of([]))), {
    initialValue: [],
  });

  // 揭示密钥弹窗（重置 / 新建后复用同一实例）。
  secretDialogVisible = signal(false);

  /** 弹窗可见性变化的唯一入口：关闭时清空 secret 与所属应用，免得下次打开显示上一次的 secret。 */
  onSecretDialogVisibleChange(visible: boolean): void {
    if (!visible) {
      this.secretValue.set('');
      this.secretApplication.set(null);
    }
    this.secretDialogVisible.set(visible);
  }
  secretValue = signal('');
  secretKind = signal<SecretRevealKind>('created');
  secretApplication = signal<Pick<OpenApplicationOutputDto, 'clientId' | 'displayName'> | null>(
    null,
  );

  // 搜索框即时值：随 URL 回填，输入时乐观更新，防抖后写回 URL。
  readonly searchQuery = signal(this.route.snapshot.queryParamMap.get('keyword') ?? '');
  // FacetedFilter 的取值由 URL 状态驱动。
  readonly selectedApplicationType = computed(
    () => (this.queryParams().get('applicationType') as OpenApplicationType | null) ?? null,
  );
  readonly selectedClientType = computed(
    () => (this.queryParams().get('clientType') as OpenApplicationClientType | null) ?? null,
  );
  // 是否处于筛选/搜索态：用于区分「暂无数据」与「无匹配结果」的空状态。
  readonly hasActiveFilters = computed(
    () =>
      this.searchQuery().trim().length > 0 ||
      this.selectedApplicationType() !== null ||
      this.selectedClientType() !== null,
  );

  //#if (IncludeLocalization)
  private readonly applicationTypeOptionsTexts = {
    web: translateSignal('openApp.appType.web', {}, { scope: 'openApp' }),
    native: translateSignal('openApp.appType.native', {}, { scope: 'openApp' }),
    service: translateSignal('openApp.appType.service', {}, { scope: 'openApp' }),
  };
  readonly applicationTypeOptions = computed(() => [
    {
      label: this.applicationTypeOptionsTexts.web(),
      value: 'web' as const,
      icon: 'lucideGlobe',
    },
    {
      label: this.applicationTypeOptionsTexts.native(),
      value: 'native' as const,
      icon: 'lucideMonitor',
    },
    {
      label: this.applicationTypeOptionsTexts.service(),
      value: 'service' as const,
      icon: 'lucideServer',
    },
  ]);

  private readonly clientTypeOptionsTexts = {
    publicLabel: translateSignal('openApp.clientType.publicLabel', {}, { scope: 'openApp' }),
    confidentialLabel: translateSignal(
      'openApp.clientType.confidentialLabel',
      {},
      { scope: 'openApp' },
    ),
  };
  readonly clientTypeOptions = computed(() => [
    {
      label: this.clientTypeOptionsTexts.publicLabel(),
      value: 'public' as const,
      icon: 'lucideUnlock',
    },
    {
      label: this.clientTypeOptionsTexts.confidentialLabel(),
      value: 'confidential' as const,
      icon: 'lucideLockKeyhole',
    },
  ]);
  //#else
  readonly applicationTypeOptions = computed(() => [
    { label: 'Web', value: 'web' as const, icon: 'lucideGlobe' },
    { label: 'Desktop/Native', value: 'native' as const, icon: 'lucideMonitor' },
    { label: 'Service', value: 'service' as const, icon: 'lucideServer' },
  ]);

  readonly clientTypeOptions = computed(() => [
    { label: 'Public', value: 'public' as const, icon: 'lucideUnlock' },
    { label: 'Confidential', value: 'confidential' as const, icon: 'lucideLockKeyhole' },
  ]);
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
            return this.service.getOpenApplications(this.queryFromParams(params)).pipe(
              catchError((error: unknown) => {
                // 已有行时刷新失败：保留旧行，只做提示
                if (this.applications().length > 0) {
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
        this.applications.set(data.items);
        this.totalRecords.set(data.totalCount);
      });

    //#if (IncludeLocalization)
    const title = translateSignal('openApp.page.title', {}, { scope: 'openApp' });
    effect(() => this.layoutService.title.set(title()));
    //#else
    this.layoutService.title.set('Open applications');
    //#endif
  }

  onSearchQueryChange(value: string) {
    this.searchQuery.set(value);
    this.searchSubject.next(value);
  }

  onApplicationTypeChange(value: OpenApplicationType | null | undefined) {
    this.updateQuery({ applicationType: value ?? null, page: 1 });
  }

  onClientTypeChange(value: OpenApplicationClientType | null | undefined) {
    this.updateQuery({ clientType: value ?? null, page: 1 });
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

  openAddDialog() {
    this.selectedApplication.set(null);
    this.editDialogVisible.set(true);
  }

  openEditDialog(id: string) {
    this.selectedApplication.set(null);
    this.editDialogVisible.set(true);
    this.editDialogLoading.set(true);

    this.service
      .getOpenApplication(id)
      .pipe(
        finalize(() => this.editDialogLoading.set(false)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: (application) => this.selectedApplication.set(application),
        error: (error) => this.showRequestError(error),
      });
  }

  handleSave(data: CreateOpenApplicationInputDto | UpdateOpenApplicationInputDto) {
    this.editDialogSaving.set(true);
    const selected = this.selectedApplication();

    const request = selected
      ? this.service.updateOpenApplication(selected.id, data as UpdateOpenApplicationInputDto)
      : this.service.createOpenApplication(data as CreateOpenApplicationInputDto);

    request
      .pipe(
        finalize(() => this.editDialogSaving.set(false)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: (result) => {
          //#if (IncludeLocalization)
          toast.success(this.transloco.translate('common.success'), {
            description: this.transloco.translate(
              selected ? 'openApp.toast.updated' : 'openApp.toast.created',
            ),
          });
          //#else
          toast.success('Success', {
            description: selected
              ? 'Open application updated successfully'
              : 'Open application created successfully',
          });
          //#endif
          this.editDialogVisible.set(false);

          // 创建 Confidential 客户端后显示自动生成的 Secret
          if (!selected && result.clientSecret) {
            this.secretValue.set(result.clientSecret);
            this.secretKind.set('created');
            this.secretApplication.set(result);
            this.secretDialogVisible.set(true);
          }

          this.reloadList();
        },
        error: (error) => this.showRequestError(error),
      });
  }

  async handleDelete(id: string) {
    const confirmed = await this.confirmService.open({
      //#if (IncludeLocalization)
      message: this.transloco.translate('openApp.confirm.deleteMessage'),
      header: this.transloco.translate('openApp.confirm.deleteHeader'),
      confirmText: this.transloco.translate('common.ok'),
      cancelText: this.transloco.translate('common.cancel'),
      //#else
      message:
        'Are you sure you want to delete this open application? Clients using this Client ID will no longer be able to sign in.',
      header: 'Confirm deletion',
      //#endif
      variant: 'destructive',
    });
    if (!confirmed) {
      return;
    }

    this.service
      .deleteOpenApplication(id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          //#if (IncludeLocalization)
          toast.success(this.transloco.translate('common.success'), {
            description: this.transloco.translate('openApp.toast.deleted'),
          });
          //#else
          toast.success('Success', { description: 'Open application deleted' });
          //#endif
          this.reloadList();
        },
        error: (error) => this.showRequestError(error),
      });
  }

  async handleResetSecret(id: string) {
    const confirmed = await this.confirmService.open({
      //#if (IncludeLocalization)
      message: this.transloco.translate('openApp.confirm.resetSecretMessage'),
      header: this.transloco.translate('openApp.confirm.resetSecretHeader'),
      confirmText: this.transloco.translate('common.ok'),
      cancelText: this.transloco.translate('common.cancel'),
      //#else
      message:
        "Are you sure you want to reset this open application's Client Secret? The old secret will be invalidated immediately.",
      header: 'Confirm secret reset',
      //#endif
      variant: 'destructive',
    });
    if (!confirmed) {
      return;
    }

    // 重置入口在列表行上，所属应用取自当前列表
    const application = this.applications().find((item) => item.id === id) ?? null;
    this.service
      .resetSecret(id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => {
          this.secretValue.set(result.clientSecret);
          this.secretKind.set('reset');
          this.secretApplication.set(application);
          this.secretDialogVisible.set(true);
          this.reloadList();
        },
        error: (error) => this.showRequestError(error),
      });
  }

  private queryFromParams(params: ParamMap): GetOpenApplicationsInputDto {
    const pagination = paginationFromQuery(params);
    return {
      offset: pagination.pageIndex * pagination.pageSize,
      limit: pagination.pageSize,
      keyword: params.get('keyword') || undefined,
      applicationType: (params.get('applicationType') as OpenApplicationType | null) ?? undefined,
      clientType: (params.get('clientType') as OpenApplicationClientType | null) ?? undefined,
      sorting: toApiSorting(
        sortingFromQuery(params, APPLICATION_SORT_COLUMNS, DEFAULT_APPLICATION_SORTING),
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
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'openApp.filter.searchPlaceholder': 'Search by name / Client ID...',
  'openApp.filter.appTypeLabel': 'Application type',
  'common.clearFilter': 'Clear filter',
  'common.noResults': 'No results',
  'openApp.filter.clientTypeLabel': 'Client type',
  'common.refresh': 'Refresh',
  'openApp.action.create': 'New Open Application',
};
//#endif
