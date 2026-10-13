import { addDays, calendarDateIn } from './dashboard-calendar';
import { formatAppDate } from '../../../../shared/pipes/app-date-pipe';

/**
 * 最近操作的时间写法。需要界面语言词条的两种（「刚刚」「昨天 HH:mm」）只给出种类，由模板翻译，
 * 词条加载与语言切换时随之更新；其余直接给出文字。
 */
export type RelativeTime =
  { kind: 'justNow' } | { kind: 'yesterday'; time: string } | { kind: 'text'; text: string };

/**
 * 按展示时区判今天、昨天：今天之内写相对时长（1 分钟内与时钟稍快导致的未来时刻都写「刚刚」），
 * 昨天写「昨天 HH:mm」，更早写月日与时刻。完整时刻由调用方放进提示。
 */
export function formatRelativeTime(
  instant: string,
  now: number,
  timeZone: string,
  locale: string | undefined,
): RelativeTime {
  const time = Date.parse(instant);
  const today = calendarDateIn(now, timeZone);
  const day = calendarDateIn(time, timeZone);

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

  if (day === addDays(today, -1)) {
    return { kind: 'yesterday', time: formatAppDate(instant, 'time', timeZone, locale) };
  }
  return { kind: 'text', text: formatAppDate(instant, 'monthDayTime', timeZone, locale) };
}
