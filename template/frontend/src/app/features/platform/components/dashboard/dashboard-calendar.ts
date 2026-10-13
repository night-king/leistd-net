/**
 * 仪表盘的日历计算：区间与预设、URL 参数还原、按周聚合与坐标轴、提示的时段文字。日期一律用
 * `YYYY-MM-DD` 字符串表示日历日，不经 `Date#toISOString()`（那是 UTC，会把展示时区的日期折偏一天）；
 * 时刻按响应回显的时区格式化，与服务端切桶用的是同一个时区。
 */
import { StatisticsGranularity } from '../../dtos/platform-statistics.dto';

/** 区间最多包含的日数，与后端一致。 */
export const MAX_RANGE_DAYS = 366;

/** 超过这么多天默认按周看；不少于 {@link WEEKLY_MIN_DAYS} 天才提供按周。 */
export const WEEKLY_DEFAULT_DAYS = 31;
export const WEEKLY_MIN_DAYS = 14;

export type RangePreset = 'today' | '7d' | '30d';

export interface CalendarRange {
  startDate: string;
  endDate: string;
}

const DATE_PATTERN = /^(\d{4})-(\d{2})-(\d{2})$/;

/** 日历日加减天数。 */
export function addDays(date: string, days: number): string {
  const [year, month, day] = date.split('-').map(Number);
  return new Date(Date.UTC(year, month - 1, day + days)).toISOString().slice(0, 10);
}

/** 两端都含的天数。 */
export function rangeDays(range: CalendarRange): number {
  return (
    (Date.parse(`${range.endDate}T00:00:00Z`) - Date.parse(`${range.startDate}T00:00:00Z`)) /
      86_400_000 +
    1
  );
}

export function presetRange(preset: RangePreset, today: string): CalendarRange {
  const days = preset === 'today' ? 1 : preset === '7d' ? 7 : 30;
  return { startDate: addDays(today, 1 - days), endDate: today };
}

/** 区间恰好是某个预设时返回它，自定义区间返回 null。 */
export function presetOf(range: CalendarRange, today: string): RangePreset | null {
  return (
    (['today', '7d', '30d'] as const).find((preset) => {
      const candidate = presetRange(preset, today);
      return candidate.startDate === range.startDate && candidate.endDate === range.endDate;
    }) ?? null
  );
}

/** 认得 `YYYY-MM-DD` 且是真实存在的日期（2 月 30 日之类不收）。 */
export function isCalendarDate(value: string | null | undefined): value is string {
  const match = value ? DATE_PATTERN.exec(value) : null;
  if (!match) {
    return false;
  }
  const date = new Date(Date.UTC(Number(match[1]), Number(match[2]) - 1, Number(match[3])));
  return date.toISOString().slice(0, 10) === value;
}

/** 从 URL 还原区间；缺一端、写坏、颠倒、超过跨度或结束日晚于今天都返回 null（由调用方回到默认区间）。 */
export function parseRange(
  startDate: string | null | undefined,
  endDate: string | null | undefined,
  today: string,
): CalendarRange | null {
  if (
    !isCalendarDate(startDate) ||
    !isCalendarDate(endDate) ||
    endDate > today ||
    startDate > endDate
  ) {
    return null;
  }
  const range = { startDate, endDate };
  return rangeDays(range) <= MAX_RANGE_DAYS ? range : null;
}

/** 日期选择器给的是浏览器本地 `Date`，它的年月日就是选中的日历日。 */
export function toCalendarDate(date: Date): string {
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`;
}

export function fromCalendarDate(date: string): Date {
  const [year, month, day] = date.split('-').map(Number);
  return new Date(year, month - 1, day);
}

/** 界面语言的一周从哪天开始（1 = 周一 … 7 = 周日）；运行时不支持 `getWeekInfo` 时取周一。 */
export function firstDayOfWeek(locale: string | undefined): number {
  try {
    const intlLocale = new Intl.Locale(locale ?? 'en') as Intl.Locale & {
      getWeekInfo?: () => { firstDay: number };
      weekInfo?: { firstDay: number };
    };
    return intlLocale.getWeekInfo?.().firstDay ?? intlLocale.weekInfo?.firstDay ?? 1;
  } catch {
    return 1;
  }
}

/** 某时刻在该时区的日历日。 */
export function calendarDateIn(instant: string | number, timeZone: string): string {
  return new Intl.DateTimeFormat('en-CA', {
    timeZone,
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
  }).format(new Date(instant));
}

/** 一周：包含的桶下标与实际覆盖的首尾日历日（首尾不完整的周按实际日期）。 */
export interface WeekGroup {
  indexes: number[];
  firstDate: string;
  lastDate: string;
}

/** 把日桶按周归组：周归属看桶在响应时区里的日历日，一周从 `firstDay` 开始。 */
export function groupByWeek(
  buckets: readonly string[],
  timeZone: string,
  firstDay: number,
): WeekGroup[] {
  const groups: WeekGroup[] = [];
  let current: WeekGroup | null = null;
  let currentWeekStart = '';
  buckets.forEach((bucket, index) => {
    const date = calendarDateIn(bucket, timeZone);
    // getUTCDay：0 = 周日；换成 1 = 周一 … 7 = 周日 再算与周起始日的距离
    const isoDay = new Date(`${date}T00:00:00Z`).getUTCDay() || 7;
    const weekStart = addDays(date, -((isoDay - firstDay + 7) % 7));
    if (!current || weekStart !== currentWeekStart) {
      current = { indexes: [], firstDate: date, lastDate: date };
      currentWeekStart = weekStart;
      groups.push(current);
    }
    current.indexes.push(index);
    current.lastDate = date;
  });
  return groups;
}

/** 按组求和；只用于可相加的序列（登录次数），存量与去重人数不能这样合并。 */
export function sumByGroups(series: readonly number[], groups: readonly WeekGroup[]): number[] {
  return groups.map((group) => group.indexes.reduce((sum, index) => sum + (series[index] ?? 0), 0));
}

/** 月 / 日，按界面语言的写法。 */
export function formatMonthDay(date: string, locale: string | undefined): string {
  return new Intl.DateTimeFormat(locale, {
    month: 'numeric',
    day: 'numeric',
    timeZone: 'UTC',
  }).format(new Date(`${date}T00:00:00Z`));
}

function formatClock(
  instant: string | number,
  timeZone: string,
  locale: string | undefined,
): string {
  return new Intl.DateTimeFormat(locale, {
    timeZone,
    hour: '2-digit',
    minute: '2-digit',
    hourCycle: 'h23',
  }).format(new Date(instant));
}

function offsetName(
  instant: string | number,
  timeZone: string,
  locale: string | undefined,
): string {
  const part = new Intl.DateTimeFormat(locale, { timeZone, timeZoneName: 'shortOffset' })
    .formatToParts(new Date(instant))
    .find((item) => item.type === 'timeZoneName');
  return part?.value ?? '';
}

/**
 * 坐标轴标签。小时桶按桶起点的真实本地钟点写 `HH:mm`（半小时时区与半小时夏令时会出现 `:30`）；
 * 夏令时回拨日同一个钟点出现两次时附上 UTC 偏移区分。日桶写月 / 日。
 */
export function bucketLabels(
  buckets: readonly string[],
  granularity: StatisticsGranularity,
  timeZone: string,
  locale: string | undefined,
): string[] {
  if (granularity === 'day') {
    return buckets.map((bucket) => formatMonthDay(calendarDateIn(bucket, timeZone), locale));
  }
  const clocks = buckets.map((bucket) => formatClock(bucket, timeZone, locale));
  return clocks.map((clock, index) =>
    clocks.indexOf(clock) !== clocks.lastIndexOf(clock)
      ? `${clock} ${offsetName(buckets[index], timeZone, locale)}`
      : clock,
  );
}

/** 周组的坐标轴标签：首尾日期，同一天时只写一个。 */
export function weekLabel(group: WeekGroup, locale: string | undefined): string {
  const first = formatMonthDay(group.firstDate, locale);
  return group.firstDate === group.lastDate
    ? first
    : `${first}–${formatMonthDay(group.lastDate, locale)}`;
}

/**
 * 提示框标题：小时桶写日期与真实起止钟点（末桶截到此刻），日桶写日期。桶跨越夏令时切换时两端的 UTC 偏移不同，
 * 各附上偏移：纽约回拨的那一小时写作 `01:00 GMT-4–01:00 GMT-5`，不会读成零长度。
 */
export function bucketTitle(
  start: string,
  end: string,
  granularity: StatisticsGranularity,
  timeZone: string,
  locale: string | undefined,
): string {
  const date = formatMonthDay(calendarDateIn(start, timeZone), locale);
  if (granularity === 'day') {
    return date;
  }
  const startOffset = offsetName(start, timeZone, locale);
  const endOffset = offsetName(end, timeZone, locale);
  const startClock = formatClock(start, timeZone, locale);
  const endClock = formatClock(end, timeZone, locale);
  return startOffset === endOffset
    ? `${date} ${startClock}–${endClock}`
    : `${date} ${startClock} ${startOffset}–${endClock} ${endOffset}`;
}
