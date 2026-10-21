import { AppDate, formatAppDate, parseAppCalendarDate } from './app-date-pipe';

/**
 * 时区换算与书写方式。时区断言是 Angular `date` 管道静默回落到浏览器时区的哨兵；书写方式按
 * locale、精度按槽位分组断言，不逐个 locale 抄 ICU 输出（CLDR 更新会让它们集体变红）。
 */
describe('AppDate', () => {
  const pipe = new AppDate();
  const utc = '2026-09-04T10:16:30Z';

  describe('time zone decides the instant', () => {
    it('converts by IANA name, rendering the same instant differently per time zone', () => {
      expect(pipe.transform(utc, 'full', 'Asia/Shanghai', 'zh-CN')).toBe('2026-09-04 18:16:30');
      expect(pipe.transform(utc, 'full', 'UTC', 'zh-CN')).toBe('2026-09-04 10:16:30');
      expect(pipe.transform(utc, 'full', 'America/New_York', 'zh-CN')).toBe('2026-09-04 06:16:30');
    });

    it('applies the daylight saving rules in effect at that time rather than a fixed offset', () => {
      // 纽约冬季 UTC-5、夏季 UTC-4：固定偏移表达不了这件事
      expect(pipe.transform('2026-01-15T12:00:00Z', 'full', 'America/New_York', 'zh-CN')).toBe(
        '2026-01-15 07:00:00',
      );
      expect(pipe.transform('2026-07-15T12:00:00Z', 'full', 'America/New_York', 'zh-CN')).toBe(
        '2026-07-15 08:00:00',
      );
    });

    it('falls back to the browser time zone for an unrecognized time zone instead of rendering the column empty', () => {
      const browserRendered = pipe.transform(utc, 'full', undefined, 'zh-CN');

      expect(pipe.transform(utc, 'full', 'Mars/Olympus', 'zh-CN')).toBe(browserRendered);
      expect(browserRendered).not.toBe('');
    });
  });

  describe('locale decides the format', () => {
    it('uses hyphens and the 24-hour clock for Chinese per GB/T 7408', () => {
      // CLDR 给 zh 的数字写法是 2026/09/04（斜杠），这里是一处刻意例外
      expect(pipe.transform(utc, 'full', 'Asia/Shanghai', 'zh-CN')).toBe('2026-09-04 18:16:30');
      expect(pipe.transform(utc, 'full', 'Asia/Shanghai', 'zh-TW')).toBe('2026-09-04 18:16:30');
    });

    // 用月份名是为了消掉数字月日的歧义。
    it('uses month names for other languages', () => {
      const us = pipe.transform(utc, 'full', 'Asia/Shanghai', 'en-US');
      const gb = pipe.transform(utc, 'full', 'Asia/Shanghai', 'en-GB');

      // 09/04 在美国是 9 月 4 日、在英国是 4 月 9 日；月份名让两边都只有一种读法
      expect(us).not.toContain('09/04');
      expect(gb).not.toContain('09/04');
      expect(us).toContain('Sep');
      expect(gb).toContain('Sep');
      // 字段顺序仍随地区变
      expect(us).not.toBe(gb);
    });

    it('leaves the 12/24-hour clock to the locale', () => {
      expect(pipe.transform(utc, 'full', 'Asia/Shanghai', 'en-US')).toContain('PM');
      expect(pipe.transform(utc, 'full', 'Asia/Shanghai', 'en-GB')).toContain('18:');
    });

    it('falls back to a fixed ISO format without a locale, independent of the environment', () => {
      const fallback = '2026-09-04 18:16:30';

      expect(pipe.transform(utc, 'full', 'Asia/Shanghai')).toBe(fallback);
      expect(pipe.transform(utc, 'full', 'Asia/Shanghai', '')).toBe(fallback);
      // 认不出的 locale 同样回落，而不是抛出去把整列打空
      expect(pipe.transform(utc, 'full', 'Asia/Shanghai', 'not a locale!!')).toBe(fallback);
    });
  });

  describe('slot decides the precision', () => {
    it('full shows seconds, short shows minutes', () => {
      expect(pipe.transform(utc, 'full', 'Asia/Shanghai', 'zh-CN')).toBe('2026-09-04 18:16:30');
      expect(pipe.transform(utc, 'short', 'Asia/Shanghai', 'zh-CN')).toBe('2026-09-04 18:16');
    });

    it('date shows only the date, time shows only the time of day', () => {
      expect(pipe.transform(utc, 'date', 'Asia/Shanghai', 'zh-CN')).toBe('2026-09-04');
      expect(pipe.transform(utc, 'time', 'Asia/Shanghai', 'zh-CN')).toBe('18:16');
    });

    // 通知列表这类窄位置：不带年份，也不带秒
    it('monthDayTime omits the year and seconds', () => {
      expect(pipe.transform(utc, 'monthDayTime', 'Asia/Shanghai', 'zh-CN')).toBe('09-04 18:16');
    });

    // 图表坐标轴、区间按钮这类只写月日的位置
    it('monthDay shows only the month and day', () => {
      expect(pipe.transform(utc, 'monthDay', 'Asia/Shanghai', 'zh-CN')).toBe('09-04');
      for (const locale of ['en-US', 'en-GB', 'ja-JP']) {
        const text = pipe.transform(utc, 'monthDay', 'UTC', locale);
        expect(text).not.toContain('2026');
        expect(text).not.toContain(':');
      }
    });

    it('precision depends only on the slot and is the same across locales', () => {
      for (const locale of ['zh-CN', 'en-US', 'en-GB', 'ja-JP']) {
        // 秒只出现在 full 上
        expect(/\d{1,2}:\d{2}:\d{2}/.test(pipe.transform(utc, 'full', 'UTC', locale))).toBe(true);
        expect(/\d{1,2}:\d{2}:\d{2}/.test(pipe.transform(utc, 'short', 'UTC', locale))).toBe(false);
      }
    });
  });

  it('renders empty and invalid values as an empty string, not Invalid Date', () => {
    expect(pipe.transform(null, 'full', 'Asia/Shanghai')).toBe('');
    expect(pipe.transform(undefined, 'full', 'Asia/Shanghai')).toBe('');
    expect(pipe.transform('', 'full', 'Asia/Shanghai')).toBe('');
    expect(pipe.transform('not-a-date', 'full', 'Asia/Shanghai')).toBe('');
  });
});

/**
 * 日期区间输入框的手输解析：显示成什么写法，就要能按同一写法认回来。
 *
 * 断言一律走"渲染 → 解析"的往返，不抄 ICU 输出，理由同上。
 */
describe('parseAppCalendarDate', () => {
  const days = [
    new Date(2026, 0, 1),
    new Date(2026, 8, 17),
    new Date(2028, 1, 29),
    new Date(2026, 11, 31),
  ];

  it('parses back the displayed date in every language', () => {
    for (const locale of ['zh-CN', 'en', 'en-GB', 'de', 'fr', 'ja']) {
      for (const day of days) {
        const shown = formatAppDate(day, 'date', undefined, locale);
        expect(parseAppCalendarDate(shown, locale)?.getTime(), `${locale}: ${shown}`).toBe(
          day.getTime(),
        );
      }
    }
  });

  it('tolerates punctuation and case differences in typed input', () => {
    const day = new Date(2026, 8, 17);
    const relaxed = formatAppDate(day, 'date', undefined, 'en').toLowerCase().replace(/,/g, '');

    expect(parseAppCalendarDate(relaxed, 'en')?.getTime()).toBe(day.getTime());
  });

  it('accepts YYYY-MM-DD in any language', () => {
    expect(parseAppCalendarDate('2026-09-17', 'en')?.getTime()).toBe(
      new Date(2026, 8, 17).getTime(),
    );
  });

  it('returns null for nonexistent dates and unrecognized text instead of rolling over to another day', () => {
    expect(parseAppCalendarDate('2026-02-31', 'zh-CN')).toBeNull();
    expect(parseAppCalendarDate('2026-02-31', 'en')).toBeNull();
    expect(parseAppCalendarDate('hello', 'en')).toBeNull();
    expect(parseAppCalendarDate('', 'en')).toBeNull();
  });
});
