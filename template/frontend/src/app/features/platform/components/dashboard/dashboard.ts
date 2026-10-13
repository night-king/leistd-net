// prettier-ignore
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
//#if (IncludeLocalization)
import { TranslocoDirective, translateSignal } from '@jsverse/transloco';
//#endif
import { NgIcon, provideIcons } from '@ng-icons/core';
// prettier-ignore
import {
  lucideArrowDownRight,
  lucideArrowUpRight,
  lucideBuilding2,
  lucideChevronRight,
  lucideCircleCheck,
  lucideDatabase,
  lucideIdCard,
  lucideLock,
  lucideMailWarning,
  lucideSettings,
  lucideShieldAlert,
  lucideShieldCheck,
  lucideUsers,
} from '@ng-icons/lucide';
import { HlmBadge } from '@spartan-ng/helm/badge';
import { HlmButton } from '@spartan-ng/helm/button';
//#if (IncludeOperationRecords && LocalIdentity)
import { HLM_CHART_THEME, HlmChartImports, hlmChartTooltip } from '@spartan-ng/helm/chart';
//#endif
import { HlmSkeleton } from '@spartan-ng/helm/skeleton';
import { HlmToggleGroupImports } from '@spartan-ng/helm/toggle-group';
import { HlmTooltipImports } from '@spartan-ng/helm/tooltip';
//#if (IncludeOperationRecords && LocalIdentity)
import { areaY, defineChart, lineY } from '@tanstack/charts';
import { scaleLinear } from '@tanstack/charts/scales/linear';
import { scalePoint } from '@tanstack/charts/scales/point';
//#endif
import { Subject, catchError, of, switchMap } from 'rxjs';

import { AuthService } from '../../../../core/services/auth-service';
import { AuthorizationService } from '../../../../core/services/authorization-service';
import { LayoutService } from '../../../../core/services/layout-service';
import { SettingContextService } from '../../../../core/settings/setting-context-service';
import { StatPanel } from '../../../../shared/components/stat-panel/stat-panel';
import { PERMISSIONS } from '../../../../shared/constants/permission.constants';
//#if (IncludeOperationRecords)
import { AppDate } from '../../../../shared/pipes/app-date-pipe';
//#endif
//#if (!IncludeLocalization)
import { englishText } from '../../../../shared/utils/english-text';
//#endif
//#if (IncludeOperationRecords)
import { OperationRecordOutputDto } from '../../dtos/operation-record.dto';
//#endif
import {
  PlatformStatisticsOutputDto,
  StatisticValueDto,
  StatisticsDays,
} from '../../dtos/platform-statistics.dto';
//#if (IncludeOperationRecords)
import { OperationRecordService } from '../../services/operation-record-service';
//#endif
import { PlatformStatisticsService } from '../../services/platform-statistics-service';
//#if (IncludeOperationRecords)
import { injectOperationActionSentence } from '../operation-records/widgets/operation-record-table/operation-action-sentence';
//#endif

/** 一张指标卡：值、与上一区间的变化。`change` 为 null 时没有可比口径（上一区间为 0 或无数据）。 */
interface KpiView {
  id: 'totalUsers' | 'newUsers' | 'activeUsers' | 'failedLogins';
  value: number;
  /** 相对上一区间的变化百分比，保留整数。 */
  change: number | null;
  /** 数值上升是不是坏事（登录失败）：决定变化徽章的配色。 */
  higherIsWorse: boolean;
}

//#if (IncludeOperationRecords && LocalIdentity)
/** 登录趋势的一个点：长表形态，一天两行（成功、失败）。 */
interface TrendRow {
  date: string;
  series: string;
  count: number;
}

//#endif
/** 需要关注的一项：计数大于 0 才出现，`route` 是处理入口。 */
interface AttentionView {
  id: 'lockedUsers' | 'adminsWithoutTwoFactor' | 'unconfirmedEmailUsers' | 'disabledTenants';
  count: number;
  icon: string;
  route: string;
  queryParams?: Record<string, string>;
}

/** 快捷入口：按权限出现，文案复用菜单词条。 */
interface QuickLinkView {
  label: string;
  icon: string;
  route: string;
}

/**
 * 平台仪表盘：问候、统计区间切换、指标卡、登录趋势、需要关注、最近操作与快捷入口。统计数据要
 * `App.Dashboard`，没有时只显示问候与快捷入口；统计按当前作用域（宿主或本租户）计算。
 */
@Component({
  selector: 'app-dashboard',
  standalone: true,
  // prettier-ignore
  imports: [
    RouterLink,
    NgIcon,
    HlmBadge,
    HlmButton,
    HlmSkeleton,
    ...HlmToggleGroupImports,
    ...HlmTooltipImports,
    StatPanel,
    //#if (IncludeOperationRecords && LocalIdentity)
    ...HlmChartImports,
    //#endif
    //#if (IncludeOperationRecords)
    AppDate,
    //#endif
    //#if (IncludeLocalization)
    TranslocoDirective,
    //#endif
  ],
  // prettier-ignore
  providers: [provideIcons({
    lucideArrowDownRight, lucideArrowUpRight, lucideBuilding2, lucideChevronRight,
    lucideCircleCheck, lucideDatabase, lucideIdCard, lucideLock, lucideMailWarning,
    lucideSettings, lucideShieldAlert, lucideShieldCheck, lucideUsers,
  })],
  templateUrl: './dashboard.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Dashboard {
  private readonly layoutService = inject(LayoutService);
  private readonly authService = inject(AuthService);
  private readonly authorization = inject(AuthorizationService);
  private readonly statisticsService = inject(PlatformStatisticsService);
  private readonly settingContext = inject(SettingContextService);
  //#if (IncludeOperationRecords)
  private readonly operationRecordService = inject(OperationRecordService);
  private readonly actionSentence = injectOperationActionSentence();
  protected readonly displayTimeZone = this.settingContext.timeZone;
  protected readonly displayLocale = this.settingContext.displayLocale;
  //#endif
  //#if (!IncludeLocalization)
  protected readonly t = englishText(ENGLISH);
  //#endif

  readonly canViewStatistics = computed(() =>
    this.authorization.has(PERMISSIONS.dashboard.default),
  );

  readonly days = signal<StatisticsDays>(7);
  readonly statistics = signal<PlatformStatisticsOutputDto | null>(null);
  /** 当前请求在途：首次加载显示骨架，已有数据时保留原内容。 */
  readonly loading = signal(false);
  readonly loadFailed = signal(false);
  private readonly statisticsRequests = new Subject<StatisticsDays>();
  private readonly destroyRef = inject(DestroyRef);

  /** 显示名优先，没有时用用户名。 */
  readonly userName = computed(() => {
    const user = this.authService.currentUser();
    return user?.displayName || user?.username || '';
  });

  /** 按会话时区取当前小时，决定早上好 / 下午好 / 晚上好。 */
  readonly greetingPart = computed<'morning' | 'afternoon' | 'evening'>(() => {
    const hour = Number(
      new Intl.DateTimeFormat('en-US', {
        hour: 'numeric',
        hourCycle: 'h23',
        timeZone: this.settingContext.timeZone() || undefined,
      }).format(new Date()),
    );
    return hour < 12 ? 'morning' : hour < 18 ? 'afternoon' : 'evening';
  });

  /** 指标卡标题的词条键，逐个写出：动态拼接的键不在 i18n 静态检查之内。 */
  protected readonly kpiTitleKeys: Record<KpiView['id'], string> = {
    totalUsers: 'platform.dashboard.kpi.totalUsers',
    newUsers: 'platform.dashboard.kpi.newUsers',
    activeUsers: 'platform.dashboard.kpi.activeUsers',
    failedLogins: 'platform.dashboard.kpi.failedLogins',
  };

  readonly kpis = computed<KpiView[]>(() => {
    const statistics = this.statistics();
    if (!statistics) {
      return [];
    }

    const view = (
      id: KpiView['id'],
      metric: StatisticValueDto | undefined,
      higherIsWorse = false,
    ): KpiView[] =>
      metric ? [{ id, value: metric.value, change: changeOf(metric), higherIsWorse }] : [];
    return [
      ...view('totalUsers', statistics.totalUsers),
      ...view('newUsers', statistics.newUsers),
      ...view('activeUsers', statistics.activeUsers),
      //#if (IncludeOperationRecords && LocalIdentity)
      ...view('failedLogins', statistics.failedLogins, true),
      //#endif
    ];
  });

  readonly attention = computed<AttentionView[]>(() => {
    //#if (LocalIdentity)
    const counts = this.statistics()?.attention;
    if (!counts) {
      return [];
    }

    const items: AttentionView[] = [
      {
        id: 'lockedUsers',
        count: counts.lockedUsers,
        icon: 'lucideLock',
        route: '/platform/users',
      },
      {
        id: 'adminsWithoutTwoFactor',
        count: counts.adminsWithoutTwoFactor,
        icon: 'lucideShieldAlert',
        route: '/platform/users',
      },
      {
        id: 'unconfirmedEmailUsers',
        count: counts.unconfirmedEmailUsers,
        icon: 'lucideMailWarning',
        route: '/platform/users',
        queryParams: { isEmailVerified: 'false' },
      },
      //#if (IncludeMultiTenancy)
      {
        id: 'disabledTenants',
        count: counts.disabledTenants ?? 0,
        icon: 'lucideBuilding2',
        route: '/platform/tenants',
      },
      //#endif
    ];
    return items.filter((item) => item.count > 0);
    //#else
    // 资源服务形态没有本地身份数据，需要关注的事项都不下发
    return [];
    //#endif
  });
  //#if (IncludeOperationRecords && LocalIdentity)

  /** 两条序列的图例与提示文案，随语言切换重建图表。 */
  //#if (IncludeLocalization)
  private readonly succeededLabel = translateSignal(
    'platform.dashboard.trend.succeeded',
    {},
    { scope: 'platform' },
  );
  private readonly failedLabel = translateSignal(
    'platform.dashboard.trend.failed',
    {},
    { scope: 'platform' },
  );
  private readonly trendAriaLabel = translateSignal(
    'platform.dashboard.trend.ariaLabel',
    {},
    { scope: 'platform' },
  );
  //#else
  private readonly succeededLabel = () => 'Succeeded';
  private readonly failedLabel = () => 'Failed';
  private readonly trendAriaLabel = () => 'Sign-ins per day';
  //#endif

  readonly loginTotal = computed(
    () => this.statistics()?.loginTrend.reduce((sum, day) => sum + day.succeeded, 0) ?? 0,
  );

  /**
   * 登录趋势：成功次数画面积与折线，失败次数画折线；横轴是 UTC 日（`MM-dd`），标签放不下时自动抽稀。
   * 颜色取图表令牌，不写色值。
   */
  readonly trendChart = computed(() => {
    const trend = this.statistics()?.loginTrend;
    if (!trend?.length) {
      return null;
    }

    const succeeded = this.succeededLabel();
    const failed = this.failedLabel();
    const rows = trend.flatMap((day): TrendRow[] => [
      { date: day.date, series: succeeded, count: day.succeeded },
      { date: day.date, series: failed, count: day.failed },
    ]);
    const succeededRows = rows.filter((row) => row.series === succeeded);
    const failedRows = rows.filter((row) => row.series === failed);

    return {
      definition: defineChart({
        marks: [
          areaY(succeededRows, {
            x: 'date',
            y: 'count',
            fill: 'var(--chart-1)',
            fillOpacity: 0.12,
          }),
          lineY(succeededRows, {
            x: 'date',
            y: 'count',
            z: 'series',
            stroke: 'var(--chart-1)',
            strokeWidth: 2,
          }),
          lineY(failedRows, {
            x: 'date',
            y: 'count',
            z: 'series',
            stroke: 'var(--chart-3)',
            strokeWidth: 2,
          }),
        ],
        scales: {
          x: {
            scale: () => scalePoint<string>(),
            axis: {
              ticks: { format: (date: string) => date.slice(5) },
              tickLabels: { thin: true },
            },
          },
          y: { scale: scaleLinear, nice: true, grid: true },
        },
        theme: HLM_CHART_THEME,
        tooltip: hlmChartTooltip<TrendRow, string, number>(),
      }),
      ariaLabel: this.trendAriaLabel(),
      height: 240,
    };
  });

  /** 有登录趋势时「需要关注」占右侧一栏，没有时占满整行。 */
  readonly hasTrend = computed(() => this.trendChart() !== null);
  //#else

  readonly hasTrend = signal(false).asReadonly();
  //#endif
  //#if (IncludeOperationRecords)

  /** 最近操作也是仪表盘统计的一部分：没有仪表盘权限时不请求、不显示。 */
  readonly canViewRecentRecords = computed(
    () => this.canViewStatistics() && this.authorization.has(PERMISSIONS.operationRecords.default),
  );
  readonly recentRecords = signal<OperationRecordOutputDto[] | null>(null);
  readonly recentRecordsFailed = signal(false);
  private readonly recentRecordRequests = new Subject<void>();

  /** 最近操作的句子，按记录 Id 取；句子模板在 operationRecords scope，随语言切换重算。 */
  protected readonly recentSentences = computed(
    () =>
      new Map(
        (this.recentRecords() ?? []).map((record) => [record.id, this.actionSentence(record)]),
      ),
  );
  //#endif

  /** 快捷入口按权限出现，与侧栏菜单同一口径；仪表盘自己不列。 */
  readonly quickLinks = computed<QuickLinkView[]>(() => {
    const links: (QuickLinkView & { permission: string })[] = [
      {
        //#if (IncludeLocalization)
        label: 'layout.sidebar.users',
        //#else
        label: 'User Management',
        //#endif
        icon: 'lucideUsers',
        route: '/platform/users',
        permission: PERMISSIONS.users.default,
      },
      {
        //#if (IncludeLocalization)
        label: 'layout.sidebar.roles',
        //#else
        label: 'Role Management',
        //#endif
        icon: 'lucideShieldCheck',
        route: '/platform/roles',
        permission: PERMISSIONS.roles.default,
      },
      //#if (LocalIdentity && IncludeMultiTenancy)
      {
        //#if (IncludeLocalization)
        label: 'layout.sidebar.tenants',
        //#else
        label: 'Tenant Management',
        //#endif
        icon: 'lucideBuilding2',
        route: '/platform/tenants',
        permission: PERMISSIONS.tenants.default,
      },
      //#endif
      //#if (OpenIddictServer)
      {
        //#if (IncludeLocalization)
        label: 'layout.sidebar.openApplications',
        //#else
        label: 'Open Applications',
        //#endif
        icon: 'lucideIdCard',
        route: '/platform/open-applications',
        permission: PERMISSIONS.openApplications.default,
      },
      //#endif
      //#if (IncludeOperationRecords)
      {
        //#if (IncludeLocalization)
        label: 'layout.sidebar.operationRecords',
        //#else
        label: 'Operation records',
        //#endif
        icon: 'lucideDatabase',
        route: '/platform/operation-records',
        permission: PERMISSIONS.operationRecords.default,
      },
      //#endif
      {
        //#if (IncludeLocalization)
        label: 'layout.sidebar.systemSettings',
        //#else
        label: 'System settings',
        //#endif
        icon: 'lucideSettings',
        route: '/platform/settings',
        permission: PERMISSIONS.settings.default,
      },
    ];
    return links.filter((link) => this.authorization.has(link.permission));
  });

  constructor() {
    //#if (IncludeLocalization)
    const title = translateSignal('platform.dashboard.title', {}, { scope: 'platform' });
    effect(() => this.layoutService.title.set(title()));
    //#else
    this.layoutService.title.set('Dashboard');
    //#endif

    // 切换区间时只保留最后一次请求；失败时保留已有数据，由模板提示刷新失败。
    this.statisticsRequests
      .pipe(
        switchMap((days) =>
          this.statisticsService.getStatistics(days).pipe(catchError(() => of(null))),
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((statistics) => {
        this.loading.set(false);
        if (statistics) {
          this.statistics.set(statistics);
        } else {
          this.loadFailed.set(true);
        }
      });

    // 权限到达后（含刷新页面时权限晚于组件）再取统计；没有权限不发请求。
    effect(() => {
      if (this.canViewStatistics() && !this.statistics() && !this.loading() && !this.loadFailed()) {
        this.loadStatistics();
      }
    });
    //#if (IncludeOperationRecords)

    // 首次加载与重试走同一个入口，只保留最后一次请求
    this.recentRecordRequests
      .pipe(
        switchMap(() =>
          this.operationRecordService
            .getOperationRecords({ offset: 0, limit: 5 })
            .pipe(catchError(() => of(null))),
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((page) => {
        if (page) {
          this.recentRecords.set(page.items);
        } else {
          this.recentRecordsFailed.set(true);
        }
      });
    // 只在可见性变为真时取一次；权限重载但结论不变时不重取
    effect(() => {
      if (this.canViewRecentRecords()) {
        this.loadRecentRecords();
      }
    });
    //#endif
  }

  /** 分段控件不可取消选中（`nullable` 为假），单选时只会给出另一档的值。 */
  setDays(value: StatisticsDays | readonly StatisticsDays[] | null | undefined): void {
    const days = Array.isArray(value) ? value[0] : value;
    if (days == null || days === this.days()) {
      return;
    }
    this.days.set(days as StatisticsDays);
    this.loadStatistics();
  }

  loadStatistics(): void {
    this.loading.set(true);
    this.loadFailed.set(false);
    this.statisticsRequests.next(this.days());
  }
  //#if (IncludeOperationRecords)

  loadRecentRecords(): void {
    this.recentRecordsFailed.set(false);
    this.recentRecordRequests.next();
  }
  //#endif
}

/** 与上一区间相比的变化百分比；上一区间没有值或为 0 时不比较。 */
function changeOf(metric: StatisticValueDto): number | null {
  if (metric.previous == null || metric.previous === 0) {
    return null;
  }
  return Math.round(((metric.value - metric.previous) / metric.previous) * 100);
}
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'platform.dashboard.greeting.morning': 'Good morning, {{name}}',
  'platform.dashboard.greeting.afternoon': 'Good afternoon, {{name}}',
  'platform.dashboard.greeting.evening': 'Good evening, {{name}}',
  'platform.dashboard.subtitle': 'Overview of the last {{days}} days',
  'platform.dashboard.noStatistics': 'Statistics require the Dashboard permission.',
  'platform.dashboard.rangeLabel': 'Statistics range',
  'platform.dashboard.range7': '7 days',
  'platform.dashboard.range30': '30 days',
  'platform.dashboard.loadFailed': "Couldn't load statistics.",
  'platform.dashboard.refreshFailed': "Couldn't refresh. Showing earlier data.",
  'platform.dashboard.kpi.totalUsers': 'Total users',
  'platform.dashboard.kpi.newUsers': 'New sign-ups',
  'platform.dashboard.kpi.activeUsers': 'Active users',
  'platform.dashboard.kpi.failedLogins': 'Failed sign-ins',
  'platform.dashboard.kpi.vsPrevious': 'vs previous {{days}} days',
  'platform.dashboard.kpi.noComparison': 'No earlier data to compare',
  //#if (IncludeOperationRecords && LocalIdentity)
  'platform.dashboard.trend.title': 'Sign-in trend',
  'platform.dashboard.trend.succeeded': 'Succeeded',
  'platform.dashboard.trend.failed': 'Failed',
  'platform.dashboard.trend.total': 'successful sign-ins in the last {{days}} days',
  'platform.dashboard.trend.failedApprox':
    'Failed sign-ins are approximate: repeated anonymous failures are sampled.',
  //#endif
  'platform.dashboard.attention.title': 'Needs attention',
  'platform.dashboard.attention.lockedUsers': 'Locked accounts: {{count}}',
  'platform.dashboard.attention.lockedUsersHint':
    'Locked after repeated failed sign-ins or by an administrator',
  'platform.dashboard.attention.adminsWithoutTwoFactor':
    'Administrators without two-factor: {{count}}',
  'platform.dashboard.attention.adminsWithoutTwoFactorHint':
    'Turn it on for accounts with elevated access',
  'platform.dashboard.attention.unconfirmedEmailUsers': 'Users with unverified email: {{count}}',
  'platform.dashboard.attention.unconfirmedEmailUsersHint':
    "They can't receive password reset emails",
  'platform.dashboard.attention.disabledTenants': 'Disabled tenants: {{count}}',
  'platform.dashboard.attention.disabledTenantsHint': "Users of a disabled tenant can't sign in",
  'platform.dashboard.attention.review': 'Review',
  'platform.dashboard.attention.allClear': 'Nothing needs attention right now.',
  'platform.dashboard.recent.title': 'Recent activity',
  'platform.dashboard.recent.viewAll': 'View all',
  'platform.dashboard.recent.empty': 'No activity yet',
  'platform.dashboard.recent.anonymous': 'Anonymous',
  'platform.dashboard.recent.rejected': 'Rejected',
  'platform.dashboard.quick.title': 'Quick links',
  'common.loadFailed': "Couldn't load the list",
  'common.retry': 'Retry',
};
//#endif
