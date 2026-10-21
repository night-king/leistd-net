import { getPlatformStatistics } from './platform-statistics';
//#if (LocalIdentity)
import { deleteUser } from './user';
//#endif
import { MockException } from '../core/models';
//#if (LocalIdentity)
import { DELETED_USERS, USERS } from '../data/user';
//#endif
import { setMockSessionUserId } from '../utils/current-user';

/** 与后端集成测试 `PlatformStatisticsTests` 同一组期望值：Mock 与后端按同一规则切区间与桶。 */
describe('platform statistics mock', () => {
  beforeEach(() => setMockSessionUserId('user_admin'));
  afterEach(() => setMockSessionUserId(null));

  const utc = (iso: string) => new Date(iso).toISOString();
  const query = (startDate: string, endDate: string, timeZone = 'UTC') => ({
    startDate,
    endDate,
    timeZone,
  });

  function errorOf(action: () => unknown): MockException | undefined {
    try {
      action();
    } catch (error) {
      return error as MockException;
    }
    return undefined;
  }

  function fieldsOf(action: () => unknown): string[] {
    const body = errorOf(action)?.error as { errors?: { field: string }[] } | undefined;
    return body?.errors?.map((error) => error.field) ?? [];
  }

  it('requires the dashboard permission', () => {
    setMockSessionUserId('user_demo');

    expect(errorOf(() => getPlatformStatistics(query('2026-01-01', '2026-01-01')))?.status).toBe(
      403,
    );
  });

  it.each([
    [{}, 'startDate'],
    [{ startDate: '2026-01-01', timeZone: 'UTC' }, 'endDate'],
    [{ startDate: '2026-01-01', endDate: '2026-01-02' }, 'timeZone'],
    [query('2026-13-01', '2026-01-02'), 'startDate'],
    [query('2026-01-01', '2026-01-02', 'Mars/Olympus'), 'timeZone'],
    [query('2026-01-01', '2026-01-02', 'China Standard Time'), 'timeZone'],
    [query('2026-01-02', '2026-01-01'), 'endDate'],
    [query('2025-01-01', '2026-01-02'), 'endDate'],
    [query('2026-01-01', '2026-01-03'), 'endDate'],
    [query('0001-01-01', '0001-01-01'), 'startDate'],
  ])('rejects %o as a field error on %s', (params, field) => {
    expect(
      fieldsOf(() => getPlatformStatistics(params, Date.parse('2026-01-02T12:00:00Z'))),
    ).toContain(field);
  });

  it('reads query values the way the interceptor passes them, as arrays', () => {
    const result = getPlatformStatistics(
      { startDate: ['2026-06-09'], endDate: ['2026-06-15'], timeZone: ['UTC'] },
      Date.parse('2026-06-15T12:00:00Z'),
    );

    expect(result.buckets).toHaveLength(7);
  });

  it('accepts the earliest range whose previous range is still representable', () => {
    expect(() =>
      getPlatformStatistics(
        query('0001-01-03', '0001-01-03', 'Pacific/Kiritimati'),
        Date.parse('2026-01-02T12:00:00Z'),
      ),
    ).not.toThrow();
  });

  it('keeps years before 100 in their own century', () => {
    // Date.UTC 会把 0–99 年读成 1900 年代
    const now = Date.parse('2026-01-02T12:00:00Z');
    const earliest = getPlatformStatistics(query('0001-01-03', '0001-01-03'), now);
    expect(earliest.range.start).toBe('0001-01-03T00:00:00.000Z');
    expect(earliest.range.end).toBe('0001-01-04T00:00:00.000Z');
    expect(earliest.range.previousStart).toBe('0001-01-02T00:00:00.000Z');
    expect(earliest.buckets[0]).toBe('0001-01-03T00:00:00.000Z');

    const acrossTheCentury = getPlatformStatistics(query('0099-12-31', '0100-01-01'), now);
    expect(acrossTheCentury.buckets).toEqual([
      '0099-12-31T00:00:00.000Z',
      '0100-01-01T00:00:00.000Z',
    ]);
    expect(acrossTheCentury.range.previousStart).toBe('0099-12-29T00:00:00.000Z');
  });

  it('accepts a range of exactly 366 days', () => {
    expect(() =>
      getPlatformStatistics(query('2025-01-02', '2026-01-02'), Date.parse('2026-01-02T12:00:00Z')),
    ).not.toThrow();
  });

  it('cuts the range at now and compares the same local progress of the previous range', () => {
    const result = getPlatformStatistics(
      query('2026-06-09', '2026-06-15'),
      Date.parse('2026-06-15T12:00:00Z'),
    );

    expect(result.range).toMatchObject({
      granularity: 'day',
      start: utc('2026-06-09T00:00:00Z'),
      end: utc('2026-06-15T12:00:00Z'),
      previousStart: utc('2026-06-02T00:00:00Z'),
      previousEnd: utc('2026-06-08T12:00:00Z'),
    });
    expect(result.buckets).toHaveLength(7);
    expect(result.totalUsers.series).toHaveLength(7);
    expect(result.newUsers.series).toHaveLength(7);
    //#if (IncludeOperationRecords && LocalIdentity)
    expect(result.succeededLogins.series).toHaveLength(7);
    expect(result.succeededLogins.value).toBe(
      result.succeededLogins.series.reduce((a, b) => a + b, 0),
    );
    expect(result.failedLoginAlerts.series).toHaveLength(7);
    expect(result.activeUsers.series).toHaveLength(7);
    //#endif
  });

  it('ends a past range at the start of the next day and puts the previous range right before it', () => {
    const result = getPlatformStatistics(
      query('2026-06-01', '2026-06-07'),
      Date.parse('2026-06-15T12:00:00Z'),
    );

    expect(result.range.end).toBe(utc('2026-06-08T00:00:00Z'));
    expect(result.range.previousStart).toBe(utc('2026-05-25T00:00:00Z'));
    expect(result.range.previousEnd).toBe(result.range.start);
  });

  it('cuts 25 hourly buckets on the New York fall-back day, keeping both local 1 AM hours', () => {
    const result = getPlatformStatistics(
      query('2025-11-02', '2025-11-02', 'America/New_York'),
      Date.parse('2025-11-03T12:00:00Z'),
    );

    expect(result.range.granularity).toBe('hour');
    expect(result.buckets).toHaveLength(25);
    expect(result.range.start).toBe(utc('2025-11-02T04:00:00Z'));
    expect(result.range.end).toBe(utc('2025-11-03T05:00:00Z'));
    expect(result.buckets).toContain(utc('2025-11-02T05:00:00Z'));
    expect(result.buckets).toContain(utc('2025-11-02T06:00:00Z'));
  });

  it('cuts 23 hourly buckets on the New York spring-forward day', () => {
    const result = getPlatformStatistics(
      query('2026-03-08', '2026-03-08', 'America/New_York'),
      Date.parse('2026-03-09T12:00:00Z'),
    );

    expect(result.buckets).toHaveLength(23);
    expect(result.range.start).toBe(utc('2026-03-08T05:00:00Z'));
    expect(result.range.end).toBe(utc('2026-03-09T04:00:00Z'));
  });

  it('moves a previous end that falls into the spring-forward gap to the first moment after it', () => {
    const result = getPlatformStatistics(
      query('2026-03-09', '2026-03-15', 'America/New_York'),
      Date.parse('2026-03-15T06:30:00Z'),
    );

    expect(result.range.end).toBe(utc('2026-03-15T06:30:00Z'));
    expect(result.range.previousStart).toBe(utc('2026-03-02T05:00:00Z'));
    expect(result.range.previousEnd).toBe(utc('2026-03-08T07:00:00Z'));
  });

  it('picks the earlier of a repeated Auckland hour for the previous end', () => {
    // 04-05 03:00 回拨到 02:00（+13 → +12）；此刻本地 04-06 02:30
    const result = getPlatformStatistics(
      query('2026-04-06', '2026-04-06', 'Pacific/Auckland'),
      Date.parse('2026-04-05T14:30:00Z'),
    );

    expect(result.range.previousEnd).toBe(utc('2026-04-04T13:30:00Z'));
  });

  it('moves an Auckland previous end in the spring-forward gap past it, despite the +13 offset', () => {
    // 09-27 02:00 跳到 03:00（+12 → +13）；此刻本地 09-28 02:15
    const result = getPlatformStatistics(
      query('2026-09-28', '2026-09-28', 'Pacific/Auckland'),
      Date.parse('2026-09-27T13:15:30Z'),
    );

    expect(result.range.start).toBe(utc('2026-09-27T11:00:00Z'));
    expect(result.range.previousStart).toBe(utc('2026-09-26T12:00:00Z'));
    expect(result.range.previousEnd).toBe(utc('2026-09-26T14:00:00Z'));
  });

  it('ends a Lord Howe half-hour shift day with a half-hour bucket', () => {
    const result = getPlatformStatistics(
      query('2026-10-04', '2026-10-04', 'Australia/Lord_Howe'),
      Date.parse('2026-10-05T00:00:00Z'),
    );

    expect(result.buckets).toHaveLength(24);
    expect(result.range.start).toBe(utc('2026-10-03T13:30:00Z'));
    expect(result.range.end).toBe(utc('2026-10-04T13:00:00Z'));
    expect(result.buckets.at(-1)).toBe(utc('2026-10-04T12:30:00Z'));
  });

  it('starts Kolkata days at half past the UTC hour', () => {
    const result = getPlatformStatistics(
      query('2026-10-04', '2026-10-05', 'Asia/Kolkata'),
      Date.parse('2026-10-06T00:00:00Z'),
    );

    expect(result.buckets).toEqual([utc('2026-10-03T18:30:00Z'), utc('2026-10-04T18:30:00Z')]);
  });

  it('has no buckets when today is asked for exactly at local midnight', () => {
    const result = getPlatformStatistics(
      query('2026-10-07', '2026-10-07'),
      Date.parse('2026-10-07T00:00:00Z'),
    );

    expect(result.buckets).toEqual([]);
    expect(result.range.start).toBe(result.range.end);
    expect(result.newUsers).toMatchObject({ value: 0, series: [] });
  });
  //#if (LocalIdentity)

  it('keeps deleted users in the earlier total and in sign-ups', () => {
    const snapshot = [...USERS];
    onTestFinished(() => {
      USERS.splice(0, USERS.length, ...snapshot);
      DELETED_USERS.length = 0;
    });
    const today = new Date().toISOString().slice(0, 10);
    const before = getPlatformStatistics(query(today, today));

    deleteUser('user_demo');
    // 删除时刻恰好等于统计截止时刻时它仍算存在（半开区间），隔一秒再统计
    const after = getPlatformStatistics(query(today, today), Date.now() + 1000);

    expect(after.totalUsers.value).toBe(before.totalUsers.value - 1);
    expect(after.totalUsers.previous).toBe(before.totalUsers.previous);
    expect(after.newUsers.value).toBe(before.newUsers.value);
  });
  //#endif
});
