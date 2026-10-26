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
import {
  TranslocoDirective,
  TranslocoService,
  translateObjectSignal,
  translateSignal,
} from '@jsverse/transloco';
//#endif
import { NgIcon, provideIcons } from '@ng-icons/core';
import {
  lucideBan,
  lucideCircleCheck,
  lucideDownload,
  lucideRefreshCw,
  lucideSearch,
} from '@ng-icons/lucide';
import { toast } from '@spartan-ng/brain/sonner';
import { HlmButton } from '@spartan-ng/helm/button';
// 只引入范围选择器与其输入框形态（官方称 Input Picker），imports 面更干净；输入框自带清空与
// 日历按钮及图标，本页不必登记 lucideCalendar / lucideX。这不减小包体：date-picker 的 index.ts
// 无条件 `export *`，同胞组件仍会进共享 chunk。
import { HlmDateRangeInput, HlmDateRangePicker } from '@spartan-ng/helm/date-picker';
import {
  HlmInputGroup,
  HlmInputGroupInput,
  HlmInputGroupAddon,
} from '@spartan-ng/helm/input-group';
// 导出与刷新按钮上的 hlmTooltip 指令要在作用域内，否则只是无效属性。
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

import { OperationRecordTable } from './widgets/operation-record-table/operation-record-table';
import { applicationErrorMessage } from '../../../../core/errors/application-http-error';
//#if (IncludeLocalization)
import { textAt } from '../../../../core/i18n/translation-text';
//#endif
import { AccountPreferencesService } from '../../../../core/services/account-preferences-service';
import { AuthorizationService } from '../../../../core/services/authorization-service';
import { LayoutService } from '../../../../core/services/layout-service';
import {
  FacetedFilter,
  type FacetedFilterOption,
} from '../../../../shared/components/faceted-filter/faceted-filter';
import { PERMISSIONS } from '../../../../shared/constants/permission.constants';
import { formatAppDate, parseAppCalendarDate } from '../../../../shared/pipes/app-date-pipe';
import { saveBlob } from '../../../../shared/utils/download-file';
//#if (!IncludeLocalization)
import { englishText } from '../../../../shared/utils/english-text';
//#endif
import { paginationFromQuery, tableStateToQuery } from '../../../../shared/utils/table-query-state';
import {
  isoToZonedDate,
  zonedEndOfDayIso,
  zonedStartOfDayIso,
} from '../../../../shared/utils/zoned-time';
import {
  ExportOperationRecordsInputDto,
  GetOperationRecordsInputDto,
  OperationRecordFilterOptionsDto,
  OperationRecordOutcome,
  OperationRecordOutputDto,
} from '../../dtos/operation-record.dto';
import { OperationRecordService } from '../../services/operation-record-service';

/**
 * 操作记录页。审计表只读，不提供新建、编辑与删除；后端契约不含排序，固定按时间倒序，
 * 因此也不提供排序入口。
 */
@Component({
  selector: 'app-operation-records',
  imports: [
    NgIcon,
    HlmButton,
    HlmInputGroup,
    HlmInputGroupInput,
    HlmInputGroupAddon,
    HlmDateRangePicker,
    HlmDateRangeInput,
    FacetedFilter,
    ...HlmTooltipImports,
    //#if (IncludeLocalization)
    TranslocoDirective,
    //#endif
    OperationRecordTable,
  ],
  providers: [
    // 图标漏登记不报错，只是不渲染。
    provideIcons({
      lucideBan,
      lucideCircleCheck,
      lucideDownload,
      lucideRefreshCw,
      lucideSearch,
    }),
  ],
  templateUrl: './operation-records.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class OperationRecords {
  private readonly service = inject(OperationRecordService);
  private readonly destroyRef = inject(DestroyRef);
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

  records = signal<OperationRecordOutputDto[]>([]);
  totalRecords = signal(0);
  loading = signal(false);
  /** 列表加载失败且没有旧行可保留时的原因：有值时表格显示错误态与重试，不显示"暂无数据"。 */
  loadError = signal<string | null>(null);

  /** 导出进行中：按钮置灰，避免连点发起多次下载。 */
  readonly exporting = signal(false);

  /** 按导出权限而非查看权限裁剪导出按钮；服务端对 `/export` 独立校验同一权限。 */
  readonly canExport = computed(() =>
    this.authorizationService.has(PERMISSIONS.operationRecords.export),
  );

  // 列表状态全部来源于 URL 查询参数，刷新、前进后退与分享均可复原。
  readonly pagination = computed(() => paginationFromQuery(this.queryParams()));

  // 搜索框即时值：随 URL 回填，输入时乐观更新，防抖后写回 URL。
  readonly searchQuery = signal(this.route.snapshot.queryParamMap.get('keyword') ?? '');

  // 筛选与列表显示同按展示时区解释时间。
  private readonly displayTimeZone = inject(AccountPreferencesService).timeZone;
  private readonly displayLocale = inject(AccountPreferencesService).displayLocale;

  /** 选中的时间范围，从 URL 回填；起止缺一端即视为未筛选。 */
  readonly selectedRange = computed<[Date, Date] | undefined>(() => {
    const params = this.queryParams();
    const start = params.get('startTime');
    const end = params.get('endTime');
    if (!start || !end) {
      return undefined;
    }

    const zone = this.displayTimeZone();
    const from = isoToZonedDate(start, zone);
    const to = isoToZonedDate(end, zone);
    return from && to ? [from, to] : undefined;
  });

  /** 服务端下发的筛选项：类别与动作码都不在前端硬编码。 */
  readonly filterOptions = signal<OperationRecordFilterOptionsDto>({ categories: [], actions: [] });

  /** 当前选中的类别、动作与结果，空串表示不筛。 */
  readonly selectedCategory = computed(() => this.queryParams().get('category') ?? '');
  readonly selectedAction = computed(() => this.queryParams().get('action') ?? '');
  readonly selectedOutcome = computed(() => this.queryParams().get('outcome') ?? '');

  /** 动作候选随选中的类别裁剪，免得选出相交为空的矛盾条件。 */
  readonly actionOptions = computed(() => {
    const category = this.selectedCategory();
    const all = this.filterOptions().actions;
    return category ? all.filter((option) => option.category === category) : all;
  });

  /**
   * 是否处于筛选态，用于区分「暂无数据」与「无匹配结果」。时间区间、类别、动作、结果
   * 任一生效都算，否则一段没有记录的时间会显示成"暂无操作记录"。
   */
  readonly hasActiveFilters = computed(
    () =>
      this.searchQuery().trim().length > 0 ||
      this.selectedRange() !== undefined ||
      this.selectedCategory() !== '' ||
      this.selectedAction() !== '' ||
      this.selectedOutcome() !== '',
  );

  //#if (IncludeLocalization)
  /** 类别、动作、结果的词条整段取成对象：词条到达与语言切换时，读它们的下拉项随之重算。 */
  private readonly categoryTexts = translateObjectSignal(
    'operationRecords.categories',
    {},
    { scope: 'operationRecords' },
  );
  private readonly actionTexts = translateObjectSignal(
    'operationRecords.actions',
    {},
    { scope: 'operationRecords' },
  );
  private readonly outcomeTexts = translateObjectSignal(
    'operationRecords.outcome',
    {},
    { scope: 'operationRecords' },
  );

  /** 类别的展示名；没有词条就退回原始类别标识。 */
  private categoryText(category: string): string {
    return textAt(this.categoryTexts(), category) ?? category;
  }

  /** 动作的展示名：复用列表的句子模板（`{{target}}` 已替换为空，去掉首尾空白），没有则退回裸码。 */
  private actionText(code: string): string {
    return textAt(this.actionTexts(), code)?.trim() ?? code;
  }

  private outcomeText(outcome: OperationRecordOutcome): string {
    return textAt(this.outcomeTexts(), outcome) ?? '';
  }
  //#else
  private categoryText(category: string): string {
    return category;
  }

  private actionText(code: string): string {
    return code;
  }

  private outcomeText(outcome: OperationRecordOutcome): string {
    return outcome === 'succeeded' ? 'Succeeded' : 'Rejected';
  }
  //#endif

  /** 结果的候选值，即后端查询串接受的 camelCase 名称。 */
  private readonly outcomeValues: readonly OperationRecordOutcome[] = ['succeeded', 'failed'];

  /** 筛选器候选项交给 `app-faceted-filter`：动作与类别会随业务增长，需要它自带的搜索。 */
  readonly categoryFilterOptions = computed<FacetedFilterOption[]>(() => {
    return this.filterOptions().categories.map((category) => ({
      value: category,
      label: this.categoryText(category),
    }));
  });

  readonly actionFilterOptions = computed<FacetedFilterOption[]>(() => {
    return this.actionOptions().map((option) => ({
      value: option.code,
      label: this.actionText(option.code),
    }));
  });

  readonly outcomeFilterOptions = computed<FacetedFilterOption[]>(() => {
    return this.outcomeValues.map((outcome) => ({
      value: outcome,
      label: this.outcomeText(outcome),
      icon: outcome === 'succeeded' ? 'lucideCircleCheck' : 'lucideBan',
    }));
  });

  constructor() {
    this.searchSubject
      .pipe(debounceTime(300), distinctUntilChanged(), takeUntilDestroyed(this.destroyRef))
      .subscribe((keyword) => this.updateQuery({ keyword: keyword.trim() || null, page: 1 }, true));

    // 搜索框即时值随 URL 回填（前进后退、分享链接）。
    effect(() => this.searchQuery.set(this.queryParams().get('keyword') ?? ''));

    // 筛选项加载一次：动作定义是启动期事实。失败时不提示，下拉为空而列表照常可用。
    this.service
      .getFilterOptions()
      .pipe(
        catchError(() => EMPTY),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((options) => this.filterOptions.set(options));

    // URL 变化或显式刷新时重新拉取列表。
    combineLatest([this.route.queryParamMap, this.refreshRequests.pipe(startWith(undefined))])
      .pipe(
        // loading 在 switchMap 内置位：switchMap 先取消旧请求（其 finalize 置 false）再订阅新请求。
        switchMap(([params]) =>
          defer(() => {
            this.loading.set(true);
            return this.service.getOperationRecords(this.queryFromParams(params)).pipe(
              catchError((error: unknown) => {
                // 已有行时刷新失败：保留旧行，只做提示
                if (this.records().length > 0) {
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
        this.records.set(data.items);
        this.totalRecords.set(data.totalCount);
      });
    //#if (IncludeLocalization)

    // 失败原因由后端按请求语言渲染，切换语言后要重取当前页。langChanges$ 订阅时先发出当前语言，
    // 比对上次的语言以免进页面白发一次请求。
    let seenLang = this.transloco.getActiveLang();
    this.transloco.langChanges$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((lang) => {
      if (lang === seenLang) {
        return;
      }

      seenLang = lang;
      this.refreshRequests.next();
    });

    const title = translateSignal('operationRecords.page.title', {}, { scope: 'operationRecords' });
    effect(() => this.layoutService.title.set(title()));
    //#else

    this.layoutService.title.set('Operation records');
    //#endif
  }

  onSearchQueryChange(value: string) {
    this.searchQuery.set(value);
    this.searchSubject.next(value);
  }

  /**
   * 三个筛选回调都先比对当前值再写 URL：写回 URL 会触发重新拉取，值未变时跳过可省一次请求，
   * 也避免浏览器历史堆出相同条目。
   */
  onCategoryChange(value: string | boolean | null): void {
    const next = value == null ? '' : String(value);
    if (next === this.selectedCategory()) {
      return;
    }
    // 换类别时清掉动作，否则两个条件可能相交为空。
    this.updateQuery({ category: next || null, action: null, page: 1 });
  }

  onActionChange(value: string | boolean | null): void {
    const next = value == null ? '' : String(value);
    if (next === this.selectedAction()) {
      return;
    }
    this.updateQuery({ action: next || null, page: 1 });
  }

  onOutcomeChange(value: string | boolean | null): void {
    const next = value == null ? '' : String(value);
    if (next === this.selectedOutcome()) {
      return;
    }
    this.updateQuery({ outcome: next || null, page: 1 });
  }

  onPaginationChange(pagination: PaginationState) {
    // 本页无排序：传空排序状态，让分页参数与其他列表页保持同一套 URL 约定。
    this.updateQuery(tableStateToQuery(pagination, []));
  }

  /**
   * 输入框里的区间文案，同时交给 `formatDates`（展示态）与 `formatInputDates`（编辑态）；
   * 两态共用，否则聚焦时文本会当场变成另一种写法。写法与时间列同源（`formatAppDate` 的
   * `date` 槽位）。写成返回函数的 `computed`：回调传进子组件不丢 `this`，切换语言时换新函数
   * 触发重渲染。
   *
   * 只读本地字段，不再套 `timeZone`：`isoToZonedDate` 返回的本地 `Date` 的年月日已是展示时区
   * 的日历日，再按时区格式化会折两遍偏移，浏览器时区领先时显示成前一天。缺一端时只渲染已选的一端。
   */
  readonly formatRange = computed(() => {
    const locale = this.displayLocale();
    // 不传时区：本地字段已是展示时区的日历日（见上）。
    const day = (date: Date | null) => (date ? formatAppDate(date, 'date', undefined, locale) : '');
    return (dates: [Date | null, Date | null]): string => {
      const [from, to] = dates;
      if (!from && !to) {
        return '';
      }

      return `${day(from)} ~ ${day(to)}`.trim();
    };
  });

  /**
   * 解析手输区间，交给 `hlm-date-range-input` 的 `parseDate`。认 {@link formatRange} 的当前
   * 语言写法与 `YYYY-MM-DD`（见 `parseAppCalendarDate`），构造浏览器本地 `Date`，与
   * `isoToZonedDate` 约定一致。解析不了返回 `null`：组件清掉区间但保留文本。
   */
  readonly parseRangeInput = computed(() => {
    const locale = this.displayLocale();
    return (value: string): [Date, Date] | null => {
      const parts = value
        .split('~')
        .map((part) => part.trim())
        .filter(Boolean);
      if (parts.length !== 2) {
        return null;
      }

      const from = parseAppCalendarDate(parts[0], locale);
      const to = parseAppCalendarDate(parts[1], locale);
      return from && to ? [from, to] : null;
    };
  });

  /**
   * 时间范围按展示时区取整天再换算成 UTC 写回 URL。后端是闭区间，终点取 23:59:59.999，
   * 否则最后一天整天查不到。
   */
  onRangeChange(range: [Date, Date] | null): void {
    if (!range?.[0] || !range[1]) {
      this.updateQuery({ startTime: null, endTime: null, page: 1 });
      return;
    }

    const zone = this.displayTimeZone();
    this.updateQuery({
      startTime: zonedStartOfDayIso(range[0], zone),
      endTime: zonedEndOfDayIso(range[1], zone),
      page: 1,
    });
  }

  reloadList() {
    this.refreshRequests.next();
  }

  /** 导出当前筛选条件下的前 N 条（后端 10000 封顶），筛选参数与列表共用 {@link filtersFromParams}。 */
  onExport(): void {
    if (this.exporting()) {
      return;
    }

    this.exporting.set(true);
    this.service
      .exportOperationRecords(this.filtersFromParams(this.queryParams()))
      .pipe(
        finalize(() => this.exporting.set(false)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        // 文件名在客户端生成：`responseType: 'blob'` 读不到 `Content-Disposition`，形状与服务端一致。
        next: (blob) =>
          saveBlob(
            blob,
            `operation-records-${new Date().toISOString().slice(0, 19).replace(/[-:T]/g, '')}.csv`,
          ),
        error: (error: unknown) => this.showExportError(error),
      });
  }

  /** 不含分页的筛选条件，列表查询与导出共用，避免导出内容与屏幕筛选结果漂移。 */
  private filtersFromParams(params: ParamMap): Omit<ExportOperationRecordsInputDto, 'limit'> {
    return {
      keyword: params.get('keyword') || undefined,
      // URL 里存的已是 UTC ISO 串，直接透传；换算只在写入 URL 时做一次。
      startTime: params.get('startTime') || undefined,
      endTime: params.get('endTime') || undefined,
      // 服务端接收数组，界面一次只筛一个，包成单元素数组。
      categories: params.get('category') ? [params.get('category')!] : undefined,
      actions: params.get('action') ? [params.get('action')!] : undefined,
      outcome: params.get('outcome') || undefined,
    };
  }

  private queryFromParams(params: ParamMap): GetOperationRecordsInputDto {
    const pagination = paginationFromQuery(params);
    return {
      offset: pagination.pageIndex * pagination.pageSize,
      limit: pagination.pageSize,
      ...this.filtersFromParams(params),
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

  private showExportError(error: unknown): void {
    //#if (IncludeLocalization)
    toast.error(this.transloco.translate('operationRecords.filter.exportFailed'), {
      description: applicationErrorMessage(error),
    });
    //#else
    toast.error('Export failed', { description: applicationErrorMessage(error) });
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
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'operationRecords.filter.searchPlaceholder': 'Search action / target / operator...',
  'operationRecords.filter.category': 'Category',
  'common.clearFilter': 'Clear filter',
  'common.noResults': 'No results',
  'operationRecords.filter.action': 'Action',
  'operationRecords.filter.outcome': 'Outcome',
  'operationRecords.filter.dateRange': 'Date range',
  'operationRecords.filter.export': 'Export',
  'common.refresh': 'Refresh',
};
//#endif
