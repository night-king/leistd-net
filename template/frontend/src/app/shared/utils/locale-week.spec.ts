import { firstDayOfWeek } from './locale-week';

/** 一周的第一天随界面语言；运行时给不出时取周一。 */
describe('firstDayOfWeek', () => {
  it('reads the first day of the week from the locale', () => {
    // 不同运行时对 en-US 的周起始支持不一：取周日或回落的周一都可接受
    expect([1, 7]).toContain(firstDayOfWeek('en-US'));
  });

  it('falls back to Monday for an unknown locale', () => {
    expect(firstDayOfWeek('not a locale')).toBe(1);
  });
});
