/**
 * 展示时区与 UTC 之间的换算：列表按展示时区渲染，接口收 UTC，筛选选中的日期须按展示时区换算，
 * 不能直接 `toISOString()`（那是浏览器时区）。只用原生 `Intl`，不引入日期库。
 */

/**
 * 实际生效的展示时区：设置值能被当前运行时的 `Intl` 使用就用它，为空或用不了（时区数据库更名等）
 * 时回落到浏览器时区。`AppDate` 管道与按时区请求数据的地方（如仪表盘统计）共用这一处判定，
 * 列表显示的时区与请求里带的时区不会不一致。
 */
export function resolveDisplayTimeZone(timeZone?: string | null): string {
  if (timeZone) {
    try {
      // 原样返回设置值：resolvedOptions() 会把 Asia/Kolkata 规范成旧名 Asia/Calcutta
      new Intl.DateTimeFormat('en-US', { timeZone });
      return timeZone;
    } catch {
      // 回落到浏览器时区
    }
  }
  return new Intl.DateTimeFormat().resolvedOptions().timeZone;
}

function resolveZone(timeZone?: string): string {
  return resolveDisplayTimeZone(timeZone);
}

/** 该时区此刻的日历日期，`YYYY-MM-DD`。 */
export function zonedToday(timeZone: string, now: Date = new Date()): string {
  return new Intl.DateTimeFormat('en-CA', {
    timeZone,
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
  }).format(now);
}

/**
 * 某个时刻在指定时区的偏移（毫秒）。
 *
 * 做法是把该时刻在目标时区的**墙上读数**当成 UTC 再取差值，这样不必内置时区数据库。
 */
function zoneOffsetMs(instant: Date, timeZone: string): number {
  const parts = new Intl.DateTimeFormat('en-US', {
    timeZone,
    hour12: false,
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
  }).formatToParts(instant);

  const read = (type: string) => Number(parts.find((part) => part.type === type)?.value ?? '0');
  // hour 在 hour12:false 下午夜可能给出 24，取模归零
  const wallAsUtc = Date.UTC(
    read('year'),
    read('month') - 1,
    read('day'),
    read('hour') % 24,
    read('minute'),
    read('second'),
  );

  // 两边对齐到整秒再相减：`Intl` 只给到秒，否则毫秒被算进偏移，23:59:59.999 会跨进次日。
  return wallAsUtc - (instant.getTime() - instant.getMilliseconds());
}

/**
 * 把"某时区里的墙上时间"换算成真实时刻。
 *
 * 迭代两次：第一次用猜测时刻的偏移，第二次用修正后时刻的偏移。夏令时切换当天，
 * 目标时刻两侧的偏移不同，只算一次会差整整一小时。
 */
function wallTimeToInstant(
  year: number,
  month: number,
  day: number,
  hour: number,
  minute: number,
  second: number,
  millisecond: number,
  timeZone: string,
): Date {
  const wallAsUtc = Date.UTC(year, month, day, hour, minute, second, millisecond);
  let instant = wallAsUtc - zoneOffsetMs(new Date(wallAsUtc), timeZone);
  instant = wallAsUtc - zoneOffsetMs(new Date(instant), timeZone);
  return new Date(instant);
}

/**
 * 取「该日期在指定时区的当天 00:00:00.000」对应的 UTC ISO 串。
 *
 * `date` 只取其年月日（选择器给的是浏览器本地 `Date`，时分秒无意义）。
 */
export function zonedStartOfDayIso(date: Date, timeZone?: string): string {
  return wallTimeToInstant(
    date.getFullYear(),
    date.getMonth(),
    date.getDate(),
    0,
    0,
    0,
    0,
    resolveZone(timeZone),
  ).toISOString();
}

/** 取「该日期在指定时区的当天 23:59:59.999」对应的 UTC ISO 串；后端是闭区间。 */
export function zonedEndOfDayIso(date: Date, timeZone?: string): string {
  return wallTimeToInstant(
    date.getFullYear(),
    date.getMonth(),
    date.getDate(),
    23,
    59,
    59,
    999,
    resolveZone(timeZone),
  ).toISOString();
}

/**
 * 把 UTC ISO 串还原成展示时区里的那一天，用于回填选择器；返回浏览器本地 `Date`，
 * 其年月日等于该时刻在 `timeZone` 里的日历日。
 */
export function isoToZonedDate(iso: string, timeZone?: string): Date | null {
  const instant = new Date(iso);
  if (Number.isNaN(instant.getTime())) {
    return null;
  }

  const parts = new Intl.DateTimeFormat('en-US', {
    timeZone: resolveZone(timeZone),
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
  }).formatToParts(instant);

  const read = (type: string) => Number(parts.find((part) => part.type === type)?.value ?? '0');
  return new Date(read('year'), read('month') - 1, read('day'));
}
