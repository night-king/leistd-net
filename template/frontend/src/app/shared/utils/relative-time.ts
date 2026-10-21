import { addCalendarDays, zonedCalendarDate } from './zoned-time';
import { formatAppDate } from '../pipes/app-date-pipe';

/**
 * 相对时间的写法（如列表里"多久以前"）。需要界面语言词条的两种（「刚刚」「昨天 HH:mm」）只给出种类，
 * 由调用方的模板翻译，词条加载与语言切换时随之更新；其余直接给出文字。
 */
export type RelativeTime =
  { kind: 'justNow' } | { kind: 'yesterday'; time: string } | { kind: 'text'; text: string };

/**
 * 按展示时区的日历日分档，扫一眼就知道远近（与 GitHub、Linear 等同一思路）：今天之内写相对时长
 * （1 分钟内与时钟稍快导致的未来时刻都写「刚刚」），昨天写「昨天 HH:mm」，一周内写「N 天前」，
 * 更早只写日期（今年省略年份）。完整时刻由调用方放进提示。
 */
export function formatRelativeTime(
  instant: string,
  now: number,
  timeZone: string,
  locale: string | undefined,
): RelativeTime {
  const time = Date.parse(instant);
  const today = zonedCalendarDate(now, timeZone);
  const day = zonedCalendarDate(time, timeZone);

  if (day >= today) {
    const minutes = Math.floor((now - time) / 60_000);
    if (minutes < 1) {
      return { kind: 'justNow' };
    }
    const relative = new Intl.RelativeTimeFormat(locale, { numeric: 'always' });
    return {
      kind: 'text',
      text:
        minutes < 60
          ? relative.format(-minutes, 'minute')
          : relative.format(-Math.floor(minutes / 60), 'hour'),
    };
  }

  if (day === addCalendarDays(today, -1)) {
    return { kind: 'yesterday', time: formatAppDate(instant, 'time', timeZone, locale) };
  }
  if (day >= addCalendarDays(today, -6)) {
    const days = (Date.parse(today) - Date.parse(day)) / 86_400_000;
    return {
      kind: 'text',
      text: new Intl.RelativeTimeFormat(locale, { numeric: 'always' }).format(-days, 'day'),
    };
  }
  const sameYear = day.slice(0, 4) === today.slice(0, 4);
  return {
    kind: 'text',
    text: formatAppDate(instant, sameYear ? 'monthDay' : 'date', timeZone, locale),
  };
}
