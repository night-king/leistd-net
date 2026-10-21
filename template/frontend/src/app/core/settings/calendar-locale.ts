import { effect, inject, provideEnvironmentInitializer, untracked } from '@angular/core';
//#if (IncludeLocalization)
import { toSignal } from '@angular/core/rxjs-interop';
import { TranslocoService } from '@jsverse/transloco';
//#endif
import { BrnCalendarI18n, injectBrnCalendarI18n } from '@spartan-ng/brain/calendar';

import { SettingContextService } from './setting-context-service';
import { firstDayOfWeek } from '../../shared/utils/locale-week';

/** 任取一个周日（2026-10-11）推出一周七天的名称，下标 0 = 周日，与日历组件的约定一致。 */
const A_SUNDAY = Date.UTC(2026, 9, 11);

/**
 * 日历（日期选择器）的星期名、月份标题、年月名与翻月按钮的读屏名跟随界面写法：组件默认星期写死英文、
 * 月份标题却按浏览器语言，中文界面里会出现"2026年10月"配"Su Mo Tu"。locale 取 `displayLocale`，
 * 与 `appDate` 同一来源；一周从哪天开始按 locale 的习惯。
 */
export function provideAppCalendarLocale() {
  return provideEnvironmentInitializer(() => {
    const i18n = injectBrnCalendarI18n();
    const displayLocale = inject(SettingContextService).displayLocale;
    //#if (IncludeLocalization)
    // toSignal 包住 selectTranslate 让它随语言切换重算——静态字符串会固定在启动时的语言上
    const transloco = inject(TranslocoService);
    const previous = toSignal(transloco.selectTranslate<string>('common.calendar.previousMonth'), {
      initialValue: 'Previous month',
    });
    const next = toSignal(transloco.selectTranslate<string>('common.calendar.nextMonth'), {
      initialValue: 'Next month',
    });
    //#endif

    effect(() => {
      //#if (IncludeLocalization)
      const config = calendarLocale(displayLocale(), previous(), next());
      //#else
      const config = calendarLocale(displayLocale(), 'Previous month', 'Next month');
      //#endif
      // use() 先读再写同一个配置 signal：不隔离读取，effect 会订阅自己的写入而无限重排
      untracked(() => i18n.use(config));
    });
  });
}

/** 按 locale 生成日历的全部文案；认不出的 locale 由 `Intl` 回落到运行时默认。 */
export function calendarLocale(
  locale: string | undefined,
  previousLabel: string,
  nextLabel: string,
): Partial<BrnCalendarI18n> {
  const format = (options: Intl.DateTimeFormatOptions) => {
    try {
      return new Intl.DateTimeFormat(locale, { timeZone: 'UTC', ...options });
    } catch {
      return new Intl.DateTimeFormat(undefined, { timeZone: 'UTC', ...options });
    }
  };
  const weekdayShort = format({ weekday: 'short' });
  const weekdayLong = format({ weekday: 'long' });
  const monthLong = format({ month: 'long', year: 'numeric' });
  const monthShort = format({ month: 'short' });
  const yearOnly = format({ year: 'numeric' });
  const dayOf = (index: number) => new Date(A_SUNDAY + index * 86_400_000);
  const monthOf = (month: number, year = 2000) => new Date(Date.UTC(year, month, 1));

  return {
    formatWeekdayName: (index) => weekdayShort.format(dayOf(index)),
    labelWeekday: (index) => weekdayLong.format(dayOf(index)),
    formatHeader: (month, year) => monthLong.format(monthOf(month, year)),
    formatMonth: (month) => monthShort.format(monthOf(month)),
    formatYear: (year) => yearOnly.format(monthOf(0, year)),
    months: () =>
      Array.from({ length: 12 }, (_, month) => monthShort.format(monthOf(month))) as ReturnType<
        BrnCalendarI18n['months']
      >,
    labelPrevious: () => previousLabel,
    labelNext: () => nextLabel,
    // ISO 写法 1 = 周一 … 7 = 周日，日历组件用 0 = 周日
    firstDayOfWeek: () =>
      (firstDayOfWeek(locale) % 7) as ReturnType<BrnCalendarI18n['firstDayOfWeek']>,
  };
}
