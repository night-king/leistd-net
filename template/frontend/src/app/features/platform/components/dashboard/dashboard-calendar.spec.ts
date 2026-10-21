import {
  bucketLabels,
  bucketTitle,
  groupByWeek,
  parseRange,
  presetOf,
  presetRange,
  sumByGroups,
  toCalendarDate,
  weekLabel,
} from './dashboard-calendar';

/**
 * 仪表盘的日历计算：区间、URL 还原、按周归组与按真实时刻写的坐标轴。日期与钟点的写法来自
 * `formatAppDate`，断言用写法固定的中文（短横线日期、24 小时制），不逐个 locale 抄 ICU 输出。
 */
describe('dashboard calendar', () => {
  const today = '2026-10-13';

  it('builds presets that end today and recognizes them back', () => {
    expect(presetRange('today', today)).toEqual({ startDate: today, endDate: today });
    expect(presetRange('7d', today)).toEqual({ startDate: '2026-10-07', endDate: today });
    expect(presetRange('30d', today)).toEqual({ startDate: '2026-09-14', endDate: today });
    expect(presetOf({ startDate: '2026-10-07', endDate: today }, today)).toBe('7d');
    expect(presetOf({ startDate: '2026-10-01', endDate: '2026-10-07' }, today)).toBeNull();
  });

  it.each([
    ['2026-10-01', null],
    ['2026-10-10', '2026-10-01'],
    ['2026-10-01', '2026-10-14'],
    ['2025-10-12', today],
    ['2026-02-30', '2026-03-01'],
    ['2026-1-01', '2026-01-02'],
  ])('rejects the URL range %s – %s', (start, end) => {
    expect(parseRange(start, end, today)).toBeNull();
  });

  it('accepts a range of exactly 366 days', () => {
    expect(parseRange('2025-10-13', today, today)).toEqual({
      startDate: '2025-10-13',
      endDate: today,
    });
  });

  it('reads picked dates by their local calendar day', () => {
    expect(toCalendarDate(new Date(2026, 0, 5, 23, 59))).toBe('2026-01-05');
  });

  it('groups daily buckets into weeks by the locale first day, keeping partial weeks', () => {
    // 2026-10-01 是周四；UTC 时区里桶起点就是当天零点
    const buckets = Array.from({ length: 10 }, (_, day) =>
      new Date(Date.UTC(2026, 9, 1 + day)).toISOString(),
    );

    const mondayFirst = groupByWeek(buckets, 'UTC', 1);
    expect(mondayFirst.map((group) => [group.firstDate, group.lastDate])).toEqual([
      ['2026-10-01', '2026-10-04'],
      ['2026-10-05', '2026-10-10'],
    ]);
    expect(sumByGroups([1, 1, 1, 1, 2, 2, 2, 2, 2, 2], mondayFirst)).toEqual([4, 12]);

    const sundayFirst = groupByWeek(buckets, 'UTC', 7);
    expect(sundayFirst.map((group) => [group.firstDate, group.lastDate])).toEqual([
      ['2026-10-01', '2026-10-03'],
      ['2026-10-04', '2026-10-10'],
    ]);
    expect(weekLabel(sundayFirst[1], 'zh-CN')).toBe('10-04–10-10');
  });

  it('assigns a bucket to the week of its local day, not its UTC day', () => {
    // 东八区 10-05 00:00 = UTC 10-04 16:00：按 UTC 会落进上一周
    const buckets = ['2026-10-03T16:00:00.000Z', '2026-10-04T16:00:00.000Z'];

    expect(groupByWeek(buckets, 'Asia/Shanghai', 1).map((group) => group.firstDate)).toEqual([
      '2026-10-04',
      '2026-10-05',
    ]);
  });

  it('marks the repeated local hour of a fall-back day with its UTC offset', () => {
    const buckets = ['2025-11-02T04:00:00Z', '2025-11-02T05:00:00Z', '2025-11-02T06:00:00Z'];

    const labels = bucketLabels(buckets, 'hour', 'America/New_York', 'zh-CN');

    expect(labels[0]).toBe('00:00');
    expect(labels[1]).toMatch(/^01:00 GMT-4$/);
    expect(labels[2]).toMatch(/^01:00 GMT-5$/);
  });

  it('writes both offsets in the title of the hour that crosses a fall-back', () => {
    expect(
      bucketTitle(
        '2026-11-01T05:00:00Z',
        '2026-11-01T06:00:00Z',
        'hour',
        'America/New_York',
        'zh-CN',
      ),
    ).toBe('11-01 01:00 GMT-4–01:00 GMT-5');
    // Lord Howe 04-05 02:00 回拨半小时（+11 → +10:30）：跨切换的那一小时本地读作 01:00–01:30
    expect(
      bucketTitle(
        '2026-04-04T14:00:00Z',
        '2026-04-04T15:00:00Z',
        'hour',
        'Australia/Lord_Howe',
        'zh-CN',
      ),
    ).toBe('04-05 01:00 GMT+11–01:30 GMT+10:30');
    expect(
      bucketTitle(
        '2026-11-01T07:00:00Z',
        '2026-11-01T08:00:00Z',
        'hour',
        'America/New_York',
        'zh-CN',
      ),
    ).toBe('11-01 02:00–03:00');
  });

  it('keeps the real minutes of buckets after a half-hour shift', () => {
    // Lord Howe 10-04 02:00 起 +10:30 → +11:00：那之后的桶起点落在本地 :30
    const labels = bucketLabels(
      ['2026-10-03T14:30:00Z', '2026-10-03T15:30:00Z'],
      'hour',
      'Australia/Lord_Howe',
      'zh-CN',
    );

    expect(labels).toEqual(['01:00', '02:30']);
    expect(
      bucketTitle(
        '2026-10-04T12:30:00Z',
        '2026-10-04T13:00:00Z',
        'hour',
        'Australia/Lord_Howe',
        'zh-CN',
      ),
    ).toBe('10-04 23:30–00:00');
  });
});
