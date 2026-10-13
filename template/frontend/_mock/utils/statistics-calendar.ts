/**
 * 统计区间的日历换算，与后端 `PlatformStatisticsAppService` 同一套规则：
 * 日期按请求时区解释，本地日起点落在跳时缺口里时取缺口之后的第一个时刻、回拨重复时取较早的那个；
 * 单日按 UTC 每小时步进切桶，多日按本地日切；区间含今天时截到此刻，上一区间按日历前移同样天数、比较相同的本地进度。
 * 只用原生 `Intl`，不引入日期库。
 */

const MINUTE = 60_000;
const HOUR = 60 * MINUTE;

export type StatisticsGranularity = 'hour' | 'day';

export interface StatisticsPeriod {
  start: number;
  end: number;
  previousStart: number;
  previousEnd: number;
  granularity: StatisticsGranularity;
  /** 本期的桶边界（毫秒时间戳），零长度区间为空。 */
  edges: number[];
}

/** 本地日历上的一个时刻（月份从 1 起）。 */
interface WallTime {
  year: number;
  month: number;
  day: number;
  hour: number;
  minute: number;
}

/** 时区能否被当前运行时的 `Intl` 使用；Windows 时区 ID 等不认的值返回 false。 */
export function isUsableTimeZone(timeZone: string): boolean {
  try {
    new Intl.DateTimeFormat('en-US', { timeZone });
    return true;
  } catch {
    return false;
  }
}

/** 某个时刻在该时区的墙上读数（精确到分钟）。 */
function wallTimeOf(instant: number, timeZone: string): WallTime {
  const parts = new Intl.DateTimeFormat('en-US', {
    timeZone,
    hourCycle: 'h23',
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
  }).formatToParts(new Date(instant));
  const read = (type: string) => Number(parts.find((part) => part.type === type)?.value ?? '0');
  return {
    year: read('year'),
    month: read('month'),
    day: read('day'),
    hour: read('hour') % 24,
    minute: read('minute'),
  };
}

/**
 * 年月日时分按 UTC 组成时间戳。不用 `Date.UTC`：它把 0–99 年当成 1900 年代，而最早可查的区间在 0001 年；
 * `setUTCFullYear` 不做这种换算，月日溢出照常进位。
 */
function utcOf(year: number, month: number, day: number, hour = 0, minute = 0): number {
  const date = new Date(0);
  date.setUTCFullYear(year, month - 1, day);
  date.setUTCHours(hour, minute, 0, 0);
  return date.getTime();
}

function wallAsUtc(wall: WallTime): number {
  return utcOf(wall.year, wall.month, wall.day, wall.hour, wall.minute);
}

/** 该时刻相对 UTC 的偏移（毫秒，按分钟取整）。 */
function offsetAt(instant: number, timeZone: string): number {
  const truncated = instant - (((instant % MINUTE) + MINUTE) % MINUTE);
  return wallAsUtc(wallTimeOf(truncated, timeZone)) - truncated;
}

/** 本地时刻换算成时间戳：缺口里的取缺口后第一个时刻，重复的取较早那个。 */
export function localToInstant(wall: WallTime, timeZone: string): number {
  const asUtc = wallAsUtc(wall);
  // UTC 偏移在 −12 到 +14 小时之间，这个墙上读数对应的时刻只可能落在 [asUtc − 14h, asUtc + 12h]；
  // 窗口两端的偏移就是切换前后的两个偏移（一天多的窗口里至多一次切换），由它们得出全部候选
  const from = asUtc - 14 * HOUR;
  const to = asUtc + 12 * HOUR;
  const before = offsetAt(from, timeZone);
  const candidates = [...new Set([asUtc - before, asUtc - offsetAt(to, timeZone)])]
    .filter((instant) => wallAsUtc(wallTimeOf(instant, timeZone)) === asUtc)
    .sort((a, b) => a - b);
  if (candidates.length > 0) {
    return candidates[0];
  }

  // 落在跳时缺口里：在窗口内找偏移切换的那一分钟，它就是缺口之后的第一个本地时刻
  let low = from;
  let high = to;
  while (high - low > MINUTE) {
    const middle = low + Math.floor((high - low) / 2 / MINUTE) * MINUTE;
    if (offsetAt(middle, timeZone) === before) {
      low = middle;
    } else {
      high = middle;
    }
  }
  return high;
}

/** `YYYY-MM-DD` 日期在该时区的第一个时刻。 */
export function dayStart(date: string, timeZone: string): number {
  const [year, month, day] = date.split('-').map(Number);
  return localToInstant({ year, month, day, hour: 0, minute: 0 }, timeZone);
}

/** 日历日期加减天数，返回 `YYYY-MM-DD`。 */
export function addDays(date: string, days: number): string {
  const [year, month, day] = date.split('-').map(Number);
  return new Date(utcOf(year, month, day + days)).toISOString().slice(0, 10);
}

/** 两个日期之间的天数差（`to - from`）。 */
export function daysBetween(from: string, to: string): number {
  return Math.round((Date.parse(`${to}T00:00:00Z`) - Date.parse(`${from}T00:00:00Z`)) / 86_400_000);
}

/** 该时区此刻的日历日期。 */
export function todayIn(timeZone: string, now: number): string {
  const wall = wallTimeOf(now, timeZone);
  return `${wall.year}-${String(wall.month).padStart(2, '0')}-${String(wall.day).padStart(2, '0')}`;
}

/** 按后端同一规则算出本期、上期与本期的桶边界。 */
export function resolvePeriod(
  startDate: string,
  endDate: string,
  timeZone: string,
  now: number,
): StatisticsPeriod {
  const days = daysBetween(startDate, endDate) + 1;
  const start = dayStart(startDate, timeZone);
  const fullEnd = dayStart(addDays(endDate, 1), timeZone);
  const end = Math.min(fullEnd, now);

  const previousStart = dayStart(addDays(startDate, -days), timeZone);
  let previousEnd: number;
  if (end === fullEnd) {
    previousEnd = dayStart(addDays(endDate, 1 - days), timeZone);
  } else {
    // 此刻的本地钟点前移同样的天数；落在缺口里时取缺口之后的第一个时刻，否则保留秒与毫秒
    const wall = wallTimeOf(now, timeZone);
    const shifted = new Date(utcOf(wall.year, wall.month, wall.day - days));
    const target: WallTime = {
      year: shifted.getUTCFullYear(),
      month: shifted.getUTCMonth() + 1,
      day: shifted.getUTCDate(),
      hour: wall.hour,
      minute: wall.minute,
    };
    const instant = localToInstant(target, timeZone);
    const inGap = wallAsUtc(wallTimeOf(instant, timeZone)) !== wallAsUtc(target);
    previousEnd = inGap ? instant : instant + (((now % MINUTE) + MINUTE) % MINUTE);
  }

  const granularity: StatisticsGranularity = days === 1 ? 'hour' : 'day';
  const edges: number[] = [];
  if (end > start) {
    if (granularity === 'hour') {
      for (let edge = start; edge < end; edge += HOUR) {
        edges.push(edge);
      }
    } else {
      for (let date = startDate; dayStart(date, timeZone) < end; date = addDays(date, 1)) {
        edges.push(dayStart(date, timeZone));
      }
    }
    edges.push(end);
  }

  return { start, end, previousStart, previousEnd, granularity, edges };
}
