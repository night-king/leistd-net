// prettier-ignore
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
//#if (IncludeLocalization)
import { TranslocoDirective, translateSignal } from '@jsverse/transloco';
//#endif
import { NgIcon, provideIcons } from '@ng-icons/core';
// prettier-ignore
import {
  lucideArrowDownRight,
  lucideArrowUpRight,
  lucideBuilding2,
  lucideCalendar,
  lucideChevronRight,
  lucideCircleCheck,
  lucideDatabase,
  lucideIdCard,
  lucideInfo,
  lucideLock,
  lucideMailWarning,
  lucideSettings,
  lucideShieldAlert,
  lucideShieldCheck,
  lucideUsers,
} from '@ng-icons/lucide';
//#if (IncludeOperationRecords)
import { HlmAvatarImports } from '@spartan-ng/helm/avatar';
//#endif
import { HlmBadge } from '@spartan-ng/helm/badge';
import { HlmButton } from '@spartan-ng/helm/button';
//#if (IncludeOperationRecords && LocalIdentity)
import { HLM_CHART_THEME, HlmChartImports, hlmChartTooltip } from '@spartan-ng/helm/chart';
//#else
import { HLM_CHART_THEME, HlmChartImports } from '@spartan-ng/helm/chart';
//#endif
import { HlmDatePickerTrigger, HlmDateRangePicker } from '@spartan-ng/helm/date-picker';
import { HlmSkeleton } from '@spartan-ng/helm/skeleton';
import { HlmToggleGroupImports } from '@spartan-ng/helm/toggle-group';
import { HlmTooltipImports } from '@spartan-ng/helm/tooltip';
//#if (IncludeOperationRecords && LocalIdentity)
import { areaY, defineChart, lineY } from '@tanstack/charts';
import { scaleLinear } from '@tanstack/charts/scales/linear';
import { scalePoint } from '@tanstack/charts/scales/point';
//#else
import { areaY, defineChart, lineY } from '@tanstack/charts';
import { scaleLinear } from '@tanstack/charts/scales/linear';
//#endif
import { Subject, catchError, interval, of, switchMap } from 'rxjs';

// prettier-ignore
import {
  CalendarRange,
  RangePreset,
  //#if (IncludeOperationRecords && LocalIdentity)
  WEEKLY_DEFAULT_DAYS,
  WEEKLY_MIN_DAYS,
  bucketLabels,
  bucketTitle,
  firstDayOfWeek,
  groupByWeek,
  sumByGroups,
  weekLabel,
  //#endif
  fromCalendarDate,
  parseRange,
  presetOf,
  presetRange,
  rangeDays,
  toCalendarDate,
} from './dashboard-calendar';
import { AuthService } from '../../../../core/services/auth-service';
import { AuthorizationService } from '../../../../core/services/authorization-service';
import { LayoutService } from '../../../../core/services/layout-service';
import { SettingContextService } from '../../../../core/settings/setting-context-service';
//#if (IncludeOperationRecords)
import { RelativeTimeLabel } from '../../../../shared/components/relative-time-label/relative-time-label';
//#endif
import { StatPanel } from '../../../../shared/components/stat-panel/stat-panel';
import { PERMISSIONS } from '../../../../shared/constants/permission.constants';
//#if (IncludeOperationRecords)
import { StatusDot } from '../../../../shared/directives/status-dot';
//#endif
import { formatAppDate } from '../../../../shared/pipes/app-date-pipe';
import { AppNumber, formatAppNumber } from '../../../../shared/pipes/app-number-pipe';
//#if (!IncludeLocalization)
import { englishText } from '../../../../shared/utils/english-text';
//#endif
import { resolveDisplayTimeZone, zonedCalendarDate } from '../../../../shared/utils/zoned-time';
//#if (IncludeOperationRecords)
import { OperationRecordOutputDto } from '../../dtos/operation-record.dto';
//#endif
import {
  PlatformStatisticsOutputDto,
  StatisticValueDto,
  StatisticsQuery,
} from '../../dtos/platform-statistics.dto';
//#if (IncludeOperationRecords)
import { OperationRecordService } from '../../services/operation-record-service';
//#endif
import { PlatformStatisticsService } from '../../services/platform-statistics-service';
//#if (IncludeOperationRecords)
import { OperationRecordDetail } from '../operation-records/widgets/operation-record-detail/operation-record-detail';
import { operationRecordActor } from '../operation-records/widgets/operation-record-detail/operation-record-texts';
import { injectOperationActionSentence } from '../operation-records/widgets/operation-record-table/operation-action-sentence';
//#endif

type KpiId = 'totalUsers' | 'newUsers' | 'activeUsers' | 'failedLogins';

/** 指标卡数值从这里起用紧凑写法（1.2万、48.2K），完整整数放进提示。 */
const COMPACT_FROM = 10_000;

/** 一张指标卡：值、与上一区间的变化与按桶序列。`change` 为 null 时没有可比口径（上一区间为 0 或无数据）。 */
interface KpiView {
  id: KpiId;
  value: number;
  /** 显示的数值：一万以上用紧凑写法。 */
  valueText: string;
  /** 紧凑写法时的完整整数，放进提示；否则为 null。 */
  valueFull: string | null;
  /** 相对上一区间的变化率（比例，按整数百分比取整，徽章配色与显示一致）。 */
  change: number | null;
  /** 数值上升是不是坏事（登录失败）：决定变化徽章的配色。 */
  higherIsWorse: boolean;
  /** 迷你趋势；没有按桶序列时为 null，不画。 */
  sparkline: ReturnType<typeof sparklineOf>;
}

//#if (IncludeOperationRecords && LocalIdentity)
/** 登录趋势的一个点：长表形态，一个时段两行（成功、失败）；`key` 是桶起点或周的首日，唯一。 */
interface TrendRow {
  key: string;
  series: 'succeeded' | 'failed';
  count: number;
}

//#endif
/** 需要关注的一项：计数大于 0 才出现，`route` 是处理入口，`severity` 决定图标的颜色。 */
interface AttentionView {
  id: 'lockedUsers' | 'adminsWithoutTwoFactor' | 'unconfirmedEmailUsers' | 'disabledTenants';
  count: number;
  icon: string;
  severity: 'destructive' | 'warning' | 'neutral';
  /** 处理动作的文案：锁定账户要"处理"，其余"查看"。 */
  action: 'handle' | 'review';
  /** 处理入口；看得到统计但没有目标页权限时为 null，只显示数量。 */
  route: string | null;
  queryParams?: Record<string, string>;
}

/** 快捷入口：按权限出现，文案复用菜单词条。 */
interface QuickLinkView {
  label: string;
  icon: string;
  route: string;
}
//#if (IncludeOperationRecords)

/** 最近操作的一行。 */
interface RecentRecordView {
  record: OperationRecordOutputDto;
  /** 操作人；匿名请求（如匿名登录失败）为 undefined，显示"匿名"。 */
  actor: string | undefined;
  /** 目标：名称优先，没有名称时取标识；只在动作未登记、句子里没有目标时显示。记录不带目标类型，不按动作码猜。 */
  target: string;
  initials: string;
  avatarClass: string;
}

/** 头像底色按操作人轮换，取图表令牌的淡底，不写色值。 */
const AVATAR_CLASSES = [
  'bg-chart-1/15 text-chart-1',
  'bg-chart-2/15 text-chart-2',
  'bg-chart-3/15 text-chart-3',
  'bg-chart-4/15 text-chart-4',
  'bg-chart-5/15 text-chart-5',
] as const;
//#endif

/**
 * 平台仪表盘：问候、统计区间（今天 / 7 天 / 30 天 / 自定义）、指标卡、登录趋势、需要关注、最近操作与快捷入口。
 * 统计数据要 `App.Dashboard`，没有时只显示问候与快捷入口；统计按当前作用域（宿主或本租户）计算。
 * 区间写在 URL 上，刷新与前进后退可还原；请求带实际生效的展示时区，服务端按它切区间与统计桶。
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
    HlmDateRangePicker,
    HlmDatePickerTrigger,
    ...HlmToggleGroupImports,
    ...HlmTooltipImports,
    ...HlmChartImports,
    StatPanel,
    AppNumber,
    //#if (IncludeOperationRecords)
    ...HlmAvatarImports,
    OperationRecordDetail,
    RelativeTimeLabel,
    StatusDot,
    //#endif
    //#if (IncludeLocalization)
    TranslocoDirective,
    //#endif
  ],
  // prettier-ignore
  providers: [provideIcons({
    lucideArrowDownRight, lucideArrowUpRight, lucideBuilding2, lucideCalendar, lucideChevronRight,
    lucideCircleCheck, lucideDatabase, lucideIdCard, lucideInfo, lucideLock, lucideMailWarning,
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
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);
  //#if (IncludeOperationRecords)
  private readonly operationRecordService = inject(OperationRecordService);
  private readonly actionSentence = injectOperationActionSentence();
  //#endif
  protected readonly displayLocale = this.settingContext.displayLocale;
  //#if (!IncludeLocalization)
  protected readonly t = englishText(ENGLISH);
  //#endif

  readonly canViewStatistics = computed(() =>
    this.authorization.has(PERMISSIONS.dashboard.default),
  );

  /** 实际生效的展示时区，与 `appDate` 同一判定；统计请求带它，服务端按它切区间。 */
  readonly timeZone = computed(() => resolveDisplayTimeZone(this.settingContext.timeZone()));

  /** 分钟级时钟：「今天」的判定与相对时间随它更新。 */
  readonly now = signal(Date.now());
  readonly today = computed(() => zonedCalendarDate(this.now(), this.timeZone()));

  private readonly queryParams = toSignal(this.route.queryParamMap, {
    initialValue: this.route.snapshot.queryParamMap,
  });

  /** URL 上的区间；没有或不合法时回到近 7 天。 */
  readonly range = computed<CalendarRange>(
    () =>
      parseRange(
        this.queryParams().get('startDate'),
        this.queryParams().get('endDate'),
        this.today(),
      ) ?? presetRange('7d', this.today()),
  );
  /** 区间恰好是某个预设时分段选中它，自定义区间不选中任何一档。 */
  readonly preset = computed(() => presetOf(this.range(), this.today()));
  readonly pickerRange = computed<[Date, Date]>(() => [
    fromCalendarDate(this.range().startDate),
    fromCalendarDate(this.range().endDate),
  ]);
  readonly maxPickerDate = computed(() => fromCalendarDate(this.today()));
  /** 区间按钮上的文字：月 / 日 – 月 / 日。 */
  readonly formatPickerRange = computed(() => {
    const locale = this.displayLocale();
    return (dates: [Date | null, Date | null]) =>
      dates
        .filter((date): date is Date => date !== null)
        .map((date) => formatAppDate(date, 'monthDay', undefined, locale))
        .join(' – ');
  });

  readonly statistics = signal<PlatformStatisticsOutputDto | null>(null);
  /** 当前请求在途：首次加载显示骨架，已有数据时保留原内容。 */
  readonly loading = signal(false);
  readonly loadFailed = signal(false);
  private readonly statisticsRequests = new Subject<StatisticsQuery>();

  /** 显示名优先，没有时用用户名。 */
  readonly userName = computed(() => {
    const user = this.authService.currentUser();
    return user?.displayName || user?.username || '';
  });

  /** 按展示时区取当前小时，决定早上好 / 下午好 / 晚上好。 */
  readonly greetingPart = computed<'morning' | 'afternoon' | 'evening'>(() => {
    const hour = Number(
      new Intl.DateTimeFormat('en-US', {
        hour: 'numeric',
        hourCycle: 'h23',
        timeZone: this.timeZone(),
      }).format(new Date(this.now())),
    );
    return hour < 12 ? 'morning' : hour < 18 ? 'afternoon' : 'evening';
  });

  /** 指标卡标题与口径说明的词条键，逐个写出：动态拼接的键不在 i18n 静态检查之内。 */
  protected readonly kpiTitleKeys: Record<KpiId, string> = {
    totalUsers: 'platform.dashboard.kpi.totalUsers',
    newUsers: 'platform.dashboard.kpi.newUsers',
    activeUsers: 'platform.dashboard.kpi.activeUsers',
    failedLogins: 'platform.dashboard.kpi.failedLogins',
  };
  protected readonly kpiInfoKeys: Record<KpiId, string> = {
    totalUsers: 'platform.dashboard.kpi.info.totalUsers',
    newUsers: 'platform.dashboard.kpi.info.newUsers',
    //#if (IncludeOperationRecords)
    activeUsers: 'platform.dashboard.kpi.info.activeUsers',
    //#else
    activeUsers: 'platform.dashboard.kpi.info.activeUsersLastLogin',
    //#endif
    failedLogins: 'platform.dashboard.kpi.info.failedLogins',
  };

  /** 统计结果对应的区间天数；刷新失败时页面仍按旧数据自己的区间标注。 */
  readonly statisticsDays = computed(() => {
    const range = this.statistics()?.range;
    return range ? rangeDays(range) : 0;
  });
  /**
   * 区间含今天时比较的是「相同进度」，已经结束的区间比较整段。按响应自己判断：截到此刻的区间，终点仍落在
   * 响应时区的结束日内；完整区间终点是次日起点。不用当前的今天——刷新失败时保留的旧数据可能属于另一个时区或昨天。
   */
  readonly includesToday = computed(() => {
    const range = this.statistics()?.range;
    return !!range && zonedCalendarDate(range.end, range.timeZone) === range.endDate;
  });

  readonly kpis = computed<KpiView[]>(() => {
    const statistics = this.statistics();
    if (!statistics) {
      return [];
    }

    const locale = this.displayLocale();
    const view = (
      id: KpiId,
      metric: StatisticValueDto | undefined,
      higherIsWorse = false,
    ): KpiView[] =>
      metric
        ? [
            {
              id,
              value: metric.value,
              valueText: formatAppNumber(
                metric.value,
                metric.value >= COMPACT_FROM ? 'compact' : 'integer',
                locale,
              ),
              valueFull:
                metric.value >= COMPACT_FROM
                  ? formatAppNumber(metric.value, 'integer', locale)
                  : null,
              change: changeOf(metric),
              higherIsWorse,
              sparkline: sparklineOf(
                metric.series,
                higherIsWorse ? 'var(--chart-3)' : 'var(--chart-1)',
              ),
            },
          ]
        : [];
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

    const items: (AttentionView & { route: string; permission: string })[] = [
      {
        id: 'lockedUsers',
        count: counts.lockedUsers,
        icon: 'lucideLock',
        severity: 'destructive',
        action: 'handle',
        route: '/platform/users',
        permission: PERMISSIONS.users.default,
      },
      {
        id: 'adminsWithoutTwoFactor',
        count: counts.adminsWithoutTwoFactor,
        icon: 'lucideShieldAlert',
        severity: 'warning',
        action: 'review',
        route: '/platform/users',
        permission: PERMISSIONS.users.default,
      },
      {
        id: 'unconfirmedEmailUsers',
        count: counts.unconfirmedEmailUsers,
        icon: 'lucideMailWarning',
        severity: 'warning',
        action: 'review',
        route: '/platform/users',
        queryParams: { isEmailVerified: 'false' },
        permission: PERMISSIONS.users.default,
      },
      //#if (IncludeMultiTenancy)
      {
        id: 'disabledTenants',
        count: counts.disabledTenants ?? 0,
        icon: 'lucideBuilding2',
        severity: 'neutral',
        action: 'review',
        route: '/platform/tenants',
        permission: PERMISSIONS.tenants.default,
      },
      //#endif
    ];
    return items
      .filter((item) => item.count > 0)
      .map(({ permission, route, ...item }) => ({
        ...item,
        route: this.authorization.has(permission) ? route : null,
      }));
    //#else
    // 资源服务形态没有本地身份数据，需要关注的事项都不下发
    return [];
    //#endif
  });
  //#if (IncludeOperationRecords && LocalIdentity)

  /** 用户在多日区间里选的按日 / 按周；切换区间时清空，回到该区间的默认粒度。 */
  readonly chosenTrendView = signal<'day' | 'week' | null>(null);
  /** 区间不少于 14 天才提供按周，超过 31 天默认按周；单日按小时，不提供切换。 */
  readonly canChooseTrendView = computed(
    () =>
      this.statistics()?.range.granularity === 'day' && this.statisticsDays() >= WEEKLY_MIN_DAYS,
  );
  readonly trendView = computed<'hour' | 'day' | 'week'>(() => {
    if (this.statistics()?.range.granularity === 'hour') {
      return 'hour';
    }
    if (!this.canChooseTrendView()) {
      return 'day';
    }
    return this.chosenTrendView() ?? (this.statisticsDays() > WEEKLY_DEFAULT_DAYS ? 'week' : 'day');
  });

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
  private readonly trendAriaLabel = () => 'Sign-ins per period';
  //#endif

  /**
   * 登录趋势：成功次数画渐变面积与直线，失败次数画直线（琥珀色）；横轴按桶的真实起点在响应时区里写，
   * 提示框显示该时段的起止与成功、失败两行。按周时把日桶按界面语言的周起始日合并。颜色取图表令牌，不写色值。
   */
  readonly trendChart = computed(() => {
    const statistics = this.statistics();
    if (!statistics?.buckets.length) {
      return null;
    }

    const { range, buckets } = statistics;
    const locale = this.displayLocale();
    const succeeded = statistics.succeededLogins.series ?? [];
    const failed = statistics.failedLogins.series ?? [];
    const ends = [...buckets.slice(1), range.end];

    let keys: string[];
    let labels: string[];
    let titles: string[];
    let succeededCounts: number[];
    let failedCounts: number[];
    if (this.trendView() === 'week') {
      const groups = groupByWeek(buckets, range.timeZone, firstDayOfWeek(locale));
      keys = groups.map((group) => group.firstDate);
      labels = groups.map((group) => weekLabel(group, locale));
      titles = labels;
      succeededCounts = sumByGroups(succeeded, groups);
      failedCounts = sumByGroups(failed, groups);
    } else {
      keys = buckets;
      labels = bucketLabels(buckets, range.granularity, range.timeZone, locale);
      titles = buckets.map((bucket, index) =>
        bucketTitle(bucket, ends[index], range.granularity, range.timeZone, locale),
      );
      succeededCounts = succeeded;
      failedCounts = failed;
    }

    const labelOf = new Map(keys.map((key, index) => [key, labels[index]]));
    const titleOf = new Map(keys.map((key, index) => [key, titles[index]]));
    const succeededRows: TrendRow[] = keys.map((key, index) => ({
      key,
      series: 'succeeded',
      count: succeededCounts[index] ?? 0,
    }));
    const failedRows: TrendRow[] = keys.map((key, index) => ({
      key,
      series: 'failed',
      count: failedCounts[index] ?? 0,
    }));
    const succeededText = this.succeededLabel();
    const failedText = this.failedLabel();

    return {
      definition: defineChart({
        gradients: [
          {
            id: 'dashboard-trend-fill',
            x1: 0,
            y1: 0,
            x2: 0,
            y2: 1,
            stops: [
              { offset: 0, color: 'var(--chart-1)', opacity: 0.3 },
              { offset: 1, color: 'var(--chart-1)', opacity: 0 },
            ],
          },
        ],
        marks: [
          areaY(succeededRows, { x: 'key', y: 'count', fill: 'url(#dashboard-trend-fill)' }),
          lineY(succeededRows, {
            x: 'key',
            y: 'count',
            z: 'series',
            stroke: 'var(--chart-1)',
            strokeWidth: 2,
          }),
          lineY(failedRows, {
            x: 'key',
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
              ticks: { format: (key: string) => labelOf.get(key) ?? '' },
              tickLabels: { thin: true },
            },
          },
          y: {
            scale: scaleLinear,
            nice: true,
            grid: true,
            axis: {
              ticks: { format: (value: number) => formatAppNumber(value, 'compact', locale) },
            },
          },
        },
        theme: HLM_CHART_THEME,
        tooltip: hlmChartTooltip<TrendRow, string, number>({
          content: (points) => {
            const key = points[0]?.datum.key ?? '';
            const countOf = (series: TrendRow['series']) =>
              points.find((point) => point.datum.series === series)?.datum.count ?? 0;
            return {
              title: titleOf.get(key) ?? '',
              rows: [
                {
                  label: succeededText,
                  value: formatAppNumber(countOf('succeeded'), 'integer', locale),
                  color: 'var(--chart-1)',
                },
                {
                  label: failedText,
                  value: formatAppNumber(countOf('failed'), 'integer', locale),
                  color: 'var(--chart-3)',
                },
              ],
            };
          },
        }),
      }),
      ariaLabel: this.trendAriaLabel(),
      height: 240,
    };
  });

  /** 趋势头部的环比，与指标卡同一规则：取整，上一区间为 0 时不比较。 */
  readonly trendChange = computed(() => {
    const succeeded = this.statistics()?.succeededLogins;
    return succeeded ? changeOf(succeeded) : null;
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
  /** 展开了详情的那条最近操作；同时只展开一条。 */
  readonly expandedRecentId = signal<string | null>(null);
  readonly recentRecordsFailed = signal(false);
  private readonly recentRecordRequests = new Subject<void>();

  /** 最近操作的句子，按记录 Id 取；句子模板在 operationRecords scope，随语言切换重算。 */
  protected readonly recentSentences = computed(
    () =>
      new Map(
        (this.recentRecords() ?? []).map((record) => [record.id, this.actionSentence(record)]),
      ),
  );

  readonly recentRows = computed<RecentRecordView[]>(() =>
    (this.recentRecords() ?? []).map((record) => {
      // 与操作记录表同一回落：服务端判定操作人就是目标（自证类动作）时取目标名
      const actor = operationRecordActor(record);
      const seed = record.actorId ?? actor ?? '';
      const hash = [...seed].reduce((sum, char) => (sum * 31 + char.charCodeAt(0)) >>> 0, 7);
      return {
        record,
        actor,
        target: record.targetName ?? record.targetId,
        initials: initialsOf(actor),
        avatarClass: AVATAR_CLASSES[hash % AVATAR_CLASSES.length],
      };
    }),
  );
  //#endif

  /**
   * 页头副标题的各段：区间说明 · 成功登录次数 · 需要处理的项数。只给出数据，由模板翻译，
   * 词条加载与语言切换时随之更新；区间按统计结果自己的区间说明，刷新失败时也不错位。
   */
  readonly summary = computed(() => {
    const statistics = this.statistics();
    if (!statistics) {
      return null;
    }
    const range = statistics.range;
    const preset = presetOf(range, this.today());
    return {
      preset,
      days: rangeDays(range),
      customText: preset
        ? ''
        : this.formatPickerRange()([
            fromCalendarDate(range.startDate),
            fromCalendarDate(range.endDate),
          ]),
      //#if (IncludeOperationRecords && LocalIdentity)
      signIns: formatAppNumber(statistics.succeededLogins.value, 'integer', this.displayLocale()),
      //#endif
      attention: this.attention().length,
    };
  });

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

  /** 请求身份：区间与生效时区；任一变化都重新请求，分钟级时钟本身不触发。 */
  private readonly query = computed<StatisticsQuery | null>(
    () => {
      if (!this.canViewStatistics()) {
        return null;
      }
      const { startDate, endDate } = this.range();
      return { startDate, endDate, timeZone: this.timeZone() };
    },
    { equal: (a, b) => JSON.stringify(a) === JSON.stringify(b) },
  );

  constructor() {
    //#if (IncludeLocalization)
    const title = translateSignal('platform.dashboard.title', {}, { scope: 'platform' });
    effect(() => this.layoutService.title.set(title()));
    //#else
    this.layoutService.title.set('Dashboard');
    //#endif

    interval(60_000)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.now.set(Date.now()));

    // 只保留最后一次请求；失败时保留已有数据，由模板提示刷新失败（旧数据按它自己的区间标注）
    this.statisticsRequests
      .pipe(
        switchMap((query) =>
          this.statisticsService.getStatistics(query).pipe(catchError(() => of(null))),
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

    // 权限到达后（含刷新页面时权限晚于组件）、区间或时区变化时取统计；没有权限不发请求
    effect(() => {
      const query = this.query();
      if (query) {
        untracked(() => {
          //#if (IncludeOperationRecords && LocalIdentity)
          this.chosenTrendView.set(null);
          //#endif
          this.request(query);
        });
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
  setPreset(value: RangePreset | readonly RangePreset[] | null | undefined): void {
    const preset = Array.isArray(value) ? value[0] : value;
    if (preset == null || preset === this.preset()) {
      return;
    }
    this.navigateTo(presetRange(preset as RangePreset, this.today()));
  }

  /** 选择器清空时回到近 7 天；选中的是浏览器本地 `Date`，按年月日取日历日。 */
  onRangePicked(range: [Date, Date] | null): void {
    this.navigateTo(
      range?.[0] && range[1]
        ? { startDate: toCalendarDate(range[0]), endDate: toCalendarDate(range[1]) }
        : presetRange('7d', this.today()),
    );
  }

  loadStatistics(): void {
    const query = this.query();
    if (query) {
      this.request(query);
    }
  }
  //#if (IncludeOperationRecords)

  loadRecentRecords(): void {
    this.recentRecordsFailed.set(false);
    this.recentRecordRequests.next();
  }

  toggleRecentDetail(id: string): void {
    this.expandedRecentId.update((current) => (current === id ? null : id));
  }
  //#endif

  private request(query: StatisticsQuery): void {
    this.loading.set(true);
    this.loadFailed.set(false);
    this.statisticsRequests.next(query);
  }

  private navigateTo(range: CalendarRange): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { startDate: range.startDate, endDate: range.endDate },
      queryParamsHandling: 'merge',
    });
  }
}

/**
 * 与上一区间相比的变化率（比例，0.29 表示 +29%），按整数百分比取整：显示与徽章配色用同一个值，
 * 不会出现"0%"却标成上升。上一区间没有值或为 0 时不比较。
 */
function changeOf(metric: StatisticValueDto): number | null {
  if (metric.previous == null || metric.previous === 0) {
    return null;
  }
  return Math.round(((metric.value - metric.previous) / metric.previous) * 100) / 100;
}

/** 指标卡里的迷你趋势：去掉坐标轴与网格，只留面积与线；不到两个点画不出趋势。 */
function sparklineOf(series: number[] | null | undefined, color: string) {
  if (!series || series.length < 2) {
    return null;
  }
  const rows = series.map((value, index) => ({ index, value }));
  // 纵轴取序列自身的最小、最大值，不从 0 起：存量这类大数的小起伏才看得出。不变的序列上下各留一格，
  // 画在中线上——贴底的线会被读成"降到了 0"。有起伏时面积的底边取纵轴下限，否则默认从 0 填到绘图区外
  const flat = Math.min(...series) === Math.max(...series);
  const low = Math.min(...series) - (flat ? 1 : 0);
  const high = Math.max(...series) + (flat ? 1 : 0);
  return {
    definition: defineChart({
      guides: false,
      margin: 2,
      // 不变的序列只画线：面积会在中线下铺出一条色带，像是一块数值
      marks: [
        ...(flat
          ? []
          : [areaY(rows, { x: 'index', y: 'value', y1: low, fill: color, fillOpacity: 0.12 })]),
        lineY(rows, { x: 'index', y: 'value', stroke: color, strokeWidth: 1.75 }),
      ],
      scales: {
        x: { scale: scaleLinear },
        y: { scale: scaleLinear().domain([low, high]) },
      },
      theme: HLM_CHART_THEME,
    }),
    ariaLabel: '',
    height: 34,
  };
}
//#if (IncludeOperationRecords)

/** 头像里的字：拉丁名取首字母（最多两个），其他取末两个字符（中文姓名的名）。 */
function initialsOf(name: string | undefined): string {
  if (!name) {
    return '?';
  }
  if (/^[A-Za-z]/.test(name)) {
    return name
      .split(/\s+/)
      .map((part) => part[0])
      .join('')
      .slice(0, 2)
      .toUpperCase();
  }
  return name.slice(-2);
}
//#endif
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'platform.dashboard.greeting.morning': 'Good morning, {{name}}',
  'platform.dashboard.greeting.afternoon': 'Good afternoon, {{name}}',
  'platform.dashboard.greeting.evening': 'Good evening, {{name}}',
  'platform.dashboard.noStatistics': 'Statistics require the Dashboard permission.',
  'platform.dashboard.rangeLabel': 'Statistics range',
  'platform.dashboard.range.today': 'Today',
  'platform.dashboard.range.days7': '7 days',
  'platform.dashboard.range.days30': '30 days',
  'platform.dashboard.range.lastDays': 'Last {{days}} days',
  'platform.dashboard.range.custom': 'Custom range',
  //#if (IncludeOperationRecords && LocalIdentity)
  'platform.dashboard.summary.signIns': '{{count}} successful sign-ins',
  //#endif
  'platform.dashboard.summary.attention': '{{count}} items need attention',
  'platform.dashboard.loadFailed': "Couldn't load statistics.",
  'platform.dashboard.refreshFailed': "Couldn't refresh. Showing earlier data.",
  'platform.dashboard.kpi.totalUsers': 'Total users',
  'platform.dashboard.kpi.newUsers': 'New sign-ups',
  'platform.dashboard.kpi.activeUsers': 'Active users',
  'platform.dashboard.kpi.failedLogins': 'Failed sign-ins',
  'platform.dashboard.kpi.infoLabel': 'About {{metric}}',
  'platform.dashboard.kpi.info.totalUsers':
    'Users that existed at the end of the range, not counting deleted ones.',
  'platform.dashboard.kpi.info.newUsers':
    'Accounts created in the range, including ones deleted afterwards.',
  //#if (IncludeOperationRecords)
  'platform.dashboard.kpi.info.activeUsers': 'Different users who signed in during the range.',
  //#else
  'platform.dashboard.kpi.info.activeUsersLastLogin':
    'Users whose latest sign-in falls in the range. Earlier sign-ins are overwritten by later ones, so past ranges are approximate.',
  //#endif
  'platform.dashboard.kpi.info.failedLogins':
    'Approximate: repeated anonymous failures are sampled.',
  'platform.dashboard.kpi.vsYesterday': 'vs same time yesterday',
  'platform.dashboard.kpi.vsPreviousProgress': 'vs same point of previous {{days}} days',
  'platform.dashboard.kpi.vsPrevious': 'vs previous {{days}} days',
  'platform.dashboard.kpi.vsPreviousEnd': 'vs end of previous range',
  'platform.dashboard.kpi.noComparison': 'No earlier data to compare',
  //#if (IncludeOperationRecords && LocalIdentity)
  'platform.dashboard.trend.title': 'Sign-in trend',
  'platform.dashboard.trend.succeeded': 'Succeeded',
  'platform.dashboard.trend.failed': 'Failed',
  'platform.dashboard.trend.total': 'successful sign-ins',
  'platform.dashboard.trend.viewLabel': 'Group by',
  'platform.dashboard.trend.byDay': 'Daily',
  'platform.dashboard.trend.byWeek': 'Weekly',
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
  'platform.dashboard.attention.handle': 'Handle',
  'platform.dashboard.attention.review': 'Review',
  'platform.dashboard.attention.allClear': 'Nothing needs attention right now.',
  'platform.dashboard.recent.title': 'Recent activity',
  'platform.dashboard.recent.viewAll': 'View all',
  'platform.dashboard.recent.empty': 'No activity yet',
  'platform.dashboard.recent.anonymous': 'Anonymous',
  'platform.dashboard.recent.succeeded': 'Succeeded',
  'platform.dashboard.recent.rejected': 'Rejected',
  'platform.dashboard.recent.impersonatedBy': 'Done by {{name}} on their behalf',
  'platform.dashboard.quick.title': 'Quick links',
  'common.details': 'View details',
  'common.loadFailed': "Couldn't load the list",
  'common.retry': 'Retry',
};
//#endif
