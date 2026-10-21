import { formatRelativeTime } from './relative-time';
import { formatAppDate } from '../pipes/app-date-pipe';

/** 相对时间：按展示时区判今天与昨天，午夜前后与时钟稍快都不出错。 */
describe('formatRelativeTime', () => {
  const at = (iso: string) => Date.parse(iso);

  it('says just now within a minute and for a clock slightly ahead', () => {
    const now = at('2026-10-13T12:00:00Z');

    expect(formatRelativeTime('2026-10-13T11:59:30Z', now, 'UTC', 'en')).toEqual({
      kind: 'justNow',
    });
    expect(formatRelativeTime('2026-10-13T12:00:20Z', now, 'UTC', 'en')).toEqual({
      kind: 'justNow',
    });
  });

  it('counts minutes and hours within today', () => {
    const now = at('2026-10-13T12:00:00Z');

    expect(formatRelativeTime('2026-10-13T11:52:00Z', now, 'UTC', 'en')).toEqual({
      kind: 'text',
      text: '8 minutes ago',
    });
    expect(formatRelativeTime('2026-10-13T09:00:00Z', now, 'UTC', 'en')).toEqual({
      kind: 'text',
      text: '3 hours ago',
    });
  });

  it('switches to yesterday right after local midnight', () => {
    // 东八区此刻 10-14 00:10；23:50 的记录是昨天，不是"20 分钟前"
    const now = at('2026-10-13T16:10:00Z');

    const result = formatRelativeTime('2026-10-13T15:50:00Z', now, 'Asia/Shanghai', 'en-GB');

    expect(result.kind).toBe('yesterday');
    expect(result.kind === 'yesterday' && result.time).toContain('23:50');
  });

  it('counts days within the past week by calendar day', () => {
    // 东八区此刻 10-14 00:10：10-11 是三天前，即使相隔不足 72 小时
    const now = at('2026-10-13T16:10:00Z');

    expect(formatRelativeTime('2026-10-11T08:00:00Z', now, 'Asia/Shanghai', 'en')).toEqual({
      kind: 'text',
      text: '3 days ago',
    });
    expect(formatRelativeTime('2026-10-07T20:00:00Z', now, 'Asia/Shanghai', 'zh-CN')).toEqual({
      kind: 'text',
      text: '6天前',
    });
  });

  it('writes only the date beyond a week, with the year only for another year', () => {
    const now = at('2026-10-13T12:00:00Z');

    const thisYear = formatRelativeTime('2026-09-17T08:57:00Z', now, 'UTC', 'zh-CN');
    expect(thisYear).toEqual({
      kind: 'text',
      text: formatAppDate('2026-09-17T08:57:00Z', 'monthDay', 'UTC', 'zh-CN'),
    });

    const lastYear = formatRelativeTime('2025-12-30T08:00:00Z', now, 'UTC', 'zh-CN');
    expect(lastYear.kind === 'text' && lastYear.text).toContain('2025');
    expect(lastYear.kind === 'text' && lastYear.text).not.toMatch(/\d{1,2}:\d{2}/);
  });
});
