import { formatRelativeTime } from './relative-time';

/** 最近操作的时间写法：按展示时区判今天与昨天，午夜前后与时钟稍快都不出错。 */
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

  it('writes the date for anything older than yesterday', () => {
    const result = formatRelativeTime(
      '2026-10-10T08:00:00Z',
      at('2026-10-13T12:00:00Z'),
      'UTC',
      'en',
    );

    expect(result.kind).toBe('text');
  });
});
