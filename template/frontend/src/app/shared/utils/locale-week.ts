/**
 * 界面语言的一周从哪天开始（ISO 写法：1 = 周一 … 7 = 周日）。仪表盘按周归组与日期选择器的星期列共用这一处判定；
 * 运行时不支持 `getWeekInfo` 或 locale 认不出时取周一。
 */
export function firstDayOfWeek(locale: string | undefined): number {
  try {
    const intlLocale = new Intl.Locale(locale ?? 'en') as Intl.Locale & {
      getWeekInfo?: () => { firstDay: number };
      weekInfo?: { firstDay: number };
    };
    return intlLocale.getWeekInfo?.().firstDay ?? intlLocale.weekInfo?.firstDay ?? 1;
  } catch {
    return 1;
  }
}
