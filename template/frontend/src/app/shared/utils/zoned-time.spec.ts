import {
  isoToZonedDate,
  resolveDisplayTimeZone,
  zonedEndOfDayIso,
  zonedStartOfDayIso,
  zonedToday,
} from './zoned-time';

/**
 * 展示时区与 UTC 的换算。时区一律显式传入，输入日期用浏览器本地 `Date` 构造（与日期选择器一致），
 * 结果不随运行机器的时区变化。
 */
describe('zoned-time', () => {
  /** 浏览器本地的某一天，与日期选择器给出的形态一致。 */
  function day(year: number, month: number, date: number): Date {
    return new Date(year, month - 1, date);
  }

  /** 只比日历日：回填选择器时它只认年月日。 */
  function calendarDay(value: Date | null): [number, number, number] | null {
    return value ? [value.getFullYear(), value.getMonth() + 1, value.getDate()] : null;
  }

  it.each([
    ['UTC', '2026-03-01T00:00:00.000Z', '2026-03-01T23:59:59.999Z'],
    ['Asia/Shanghai', '2026-02-28T16:00:00.000Z', '2026-03-01T15:59:59.999Z'],
    // 半小时偏移：只按整小时算偏移的实现会差 30 分钟
    ['Asia/Kolkata', '2026-02-28T18:30:00.000Z', '2026-03-01T18:29:59.999Z'],
    ['America/Los_Angeles', '2026-03-01T08:00:00.000Z', '2026-03-02T07:59:59.999Z'],
    // 日界线两侧的极端偏移（+14 与 -11）
    ['Pacific/Kiritimati', '2026-02-28T10:00:00.000Z', '2026-03-01T09:59:59.999Z'],
    ['Pacific/Pago_Pago', '2026-03-01T11:00:00.000Z', '2026-03-02T10:59:59.999Z'],
  ])('converts a whole day in %s to its UTC bounds', (zone, start, end) => {
    expect(zonedStartOfDayIso(day(2026, 3, 1), zone)).toBe(start);
    expect(zonedEndOfDayIso(day(2026, 3, 1), zone)).toBe(end);
  });

  // 夏令时切换当天两端的偏移不同：只按一个偏移换算，上界或下界会差整整一小时
  it('uses the standard offset at midnight and the summer offset at day end when DST starts', () => {
    // 纽约 2026-03-08 02:00 拨快：零点仍是 EST(-5)，当天结束时已是 EDT(-4)
    expect(zonedStartOfDayIso(day(2026, 3, 8), 'America/New_York')).toBe(
      '2026-03-08T05:00:00.000Z',
    );
    expect(zonedEndOfDayIso(day(2026, 3, 8), 'America/New_York')).toBe('2026-03-09T03:59:59.999Z');
  });

  it('uses the summer offset at midnight and the standard offset at day end when DST ends', () => {
    // 纽约 2026-11-01 02:00 拨回：这一天有 25 个小时
    expect(zonedStartOfDayIso(day(2026, 11, 1), 'America/New_York')).toBe(
      '2026-11-01T04:00:00.000Z',
    );
    expect(zonedEndOfDayIso(day(2026, 11, 1), 'America/New_York')).toBe('2026-11-02T04:59:59.999Z');
  });

  it('applies the DST shift on the southern hemisphere calendar too', () => {
    // 悉尼 2026-04-05 03:00 拨回（AEDT +11 → AEST +10），2026-10-04 02:00 拨快
    expect(zonedStartOfDayIso(day(2026, 4, 5), 'Australia/Sydney')).toBe(
      '2026-04-04T13:00:00.000Z',
    );
    expect(zonedEndOfDayIso(day(2026, 4, 5), 'Australia/Sydney')).toBe('2026-04-05T13:59:59.999Z');
    expect(zonedStartOfDayIso(day(2026, 10, 4), 'Australia/Sydney')).toBe(
      '2026-10-03T14:00:00.000Z',
    );
    expect(zonedEndOfDayIso(day(2026, 10, 4), 'Australia/Sydney')).toBe('2026-10-04T12:59:59.999Z');
  });

  it('ignores the time of day carried by the picked date', () => {
    const late = new Date(2026, 2, 1, 23, 45, 30, 500);

    expect(zonedStartOfDayIso(late, 'Asia/Shanghai')).toBe('2026-02-28T16:00:00.000Z');
    expect(zonedEndOfDayIso(late, 'Asia/Shanghai')).toBe('2026-03-01T15:59:59.999Z');
  });

  // 上界带 .999 毫秒：偏移若把这部分毫秒也算进去，上界会被推后近一秒、跨进次日，
  // 反解回选择器就显示成第二天
  it.each(['UTC', 'Asia/Shanghai', 'Asia/Kolkata', 'America/New_York', 'Pacific/Kiritimati'])(
    'keeps the end-of-day bound on the picked day in %s, including DST days',
    (zone) => {
      for (const picked of [
        day(2026, 3, 1),
        day(2026, 3, 8),
        day(2026, 11, 1),
        day(2026, 12, 31),
      ]) {
        const end = zonedEndOfDayIso(picked, zone);

        expect(end).toMatch(/:59\.999Z$/);
        expect(calendarDay(isoToZonedDate(end, zone))).toEqual(calendarDay(picked));
        expect(calendarDay(isoToZonedDate(zonedStartOfDayIso(picked, zone), zone))).toEqual(
          calendarDay(picked),
        );
      }
    },
  );

  it('makes the two bounds of one day exactly one millisecond short of the day length', () => {
    const length = (picked: Date, zone: string) =>
      Date.parse(zonedEndOfDayIso(picked, zone)) - Date.parse(zonedStartOfDayIso(picked, zone));
    const hours = (count: number) => count * 3_600_000 - 1;

    expect(length(day(2026, 6, 15), 'America/New_York')).toBe(hours(24));
    expect(length(day(2026, 3, 8), 'America/New_York')).toBe(hours(23));
    expect(length(day(2026, 11, 1), 'America/New_York')).toBe(hours(25));
  });

  it('restores the calendar day in the display zone, not in the browser zone', () => {
    // 同一时刻：上海已是 3 月 1 日，UTC 仍是 2 月 28 日
    const instant = '2026-02-28T16:00:00.000Z';

    const shanghai = isoToZonedDate(instant, 'Asia/Shanghai');
    expect(calendarDay(shanghai)).toEqual([2026, 3, 1]);
    // 返回浏览器本地零点：选择器只认年月日，带时分会在某些时区被显示成另一天
    expect([shanghai!.getHours(), shanghai!.getMinutes(), shanghai!.getSeconds()]).toEqual([
      0, 0, 0,
    ]);
    expect(calendarDay(isoToZonedDate(instant, 'UTC'))).toEqual([2026, 2, 28]);
  });

  it('returns null for a value that is not a date', () => {
    expect(isoToZonedDate('not-a-date', 'UTC')).toBeNull();
    expect(isoToZonedDate('', 'UTC')).toBeNull();
  });

  // 与 AppDate 管道同一口径：未设置展示时区时按浏览器默认时区换算
  it('falls back to the browser time zone when no display zone is set', () => {
    const browserZone = new Intl.DateTimeFormat().resolvedOptions().timeZone;
    const picked = day(2026, 3, 8);

    expect(zonedStartOfDayIso(picked)).toBe(zonedStartOfDayIso(picked, browserZone));
    expect(zonedEndOfDayIso(picked, '')).toBe(zonedEndOfDayIso(picked, browserZone));
    expect(calendarDay(isoToZonedDate(zonedEndOfDayIso(picked)))).toEqual([2026, 3, 8]);
  });

  it('uses a configured time zone the runtime can render and the browser one otherwise', () => {
    const browser = new Intl.DateTimeFormat().resolvedOptions().timeZone;

    expect(resolveDisplayTimeZone('Asia/Kolkata')).toBe('Asia/Kolkata');
    expect(resolveDisplayTimeZone(null)).toBe(browser);
    expect(resolveDisplayTimeZone('Mars/Olympus')).toBe(browser);
  });

  it('tells today by the calendar of the given time zone', () => {
    const instant = new Date('2026-10-13T20:00:00Z');

    expect(zonedToday('UTC', instant)).toBe('2026-10-13');
    expect(zonedToday('Asia/Shanghai', instant)).toBe('2026-10-14');
  });
});
