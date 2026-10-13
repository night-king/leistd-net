import { AppNumber, formatAppNumber } from './app-number-pipe';

/**
 * 数字写法：槽位决定精度与形态，locale 决定分组符号与紧凑单位。断言按业务例子写，
 * 精确字符串只取写法稳定的 en-US 与 zh-CN，不逐个 locale 抄 ICU 输出。
 */
describe('AppNumber', () => {
  const pipe = new AppNumber();

  it('groups thousands for counts', () => {
    expect(pipe.transform(1284, 'integer', 'en-US')).toBe('1,284');
    expect(pipe.transform(29846, 'integer', 'zh-CN')).toBe('29,846');
    expect(pipe.transform(0, 'integer', 'en-US')).toBe('0');
  });

  it('shortens large numbers with the unit of the locale', () => {
    expect(pipe.transform(48200, 'compact', 'en-US')).toBe('48.2K');
    expect(pipe.transform(48200, 'compact', 'zh-CN')).toBe('4.8万');
    expect(pipe.transform(950, 'compact', 'en-US')).toBe('950');
  });

  // 输入是比例：格式化处是唯一乘以 100 的地方，调用方不先算成百分数
  it('writes a change ratio as a signed percentage', () => {
    const change = (previous: number, current: number) => (current - previous) / previous;

    expect(pipe.transform(change(100, 129), 'change', 'en-US')).toBe('+29%');
    expect(pipe.transform(change(100, 80), 'change', 'en-US')).toBe('-20%');
    expect(pipe.transform(change(100, 100), 'change', 'en-US')).toBe('0%');
  });

  it('renders missing and non-finite values as an empty string', () => {
    expect(formatAppNumber(null)).toBe('');
    expect(formatAppNumber(undefined)).toBe('');
    expect(formatAppNumber(Number.NaN)).toBe('');
  });

  it('falls back to a fixed locale without or with an unknown locale', () => {
    expect(formatAppNumber(1284, 'integer')).toBe('1,284');
    expect(formatAppNumber(1284, 'integer', 'not a locale')).toBe('1,284');
  });
});
