import { Pipe, PipeTransform } from '@angular/core';

import { resolveDisplayTimeZone } from '../utils/zoned-time';

/**
 * 语义槽位：调用点按用途声明显示什么、要多细。精度属于槽位而不是用户偏好：通知列表要窄，
 * 审计详情要能对时。
 */
const SLOTS = {
  full: { date: 'full', time: true, seconds: true },
  short: { date: 'full', time: true, seconds: false },
  date: { date: 'full', time: false, seconds: false },
  time: { date: 'none', time: true, seconds: false },
  monthDayTime: { date: 'monthDay', time: true, seconds: false },
  monthDay: { date: 'monthDay', time: false, seconds: false },
} as const;

export type AppDateFormat = keyof typeof SLOTS;

/** 一种书写方式：用哪个 locale 渲染，月和日取什么形态，以及要不要固定 24 小时制。 */
interface Writing {
  locale: string;
  month: 'short' | '2-digit';
  day: 'numeric' | '2-digit';
  hour12?: boolean;
  /** en-CA 会在日期与时刻之间插一个逗号，`YYYY-MM-DD HH:mm:ss` 这种写法要去掉它。 */
  stripComma: boolean;
}

/** `YYYY-MM-DD HH:mm:ss`，24 小时制；也是拿不到界面语言时的回落：不随环境漂移，年月日顺序固定。 */
const ISO_DASHED: Writing = {
  locale: 'en-CA',
  month: '2-digit',
  day: '2-digit',
  hour12: false,
  stripComma: true,
};

/**
 * 按界面语言的地区习惯书写，月份用名称：数字月日跨地区有歧义（`09/04/2026` 美英读法相反）。
 * 12/24 小时制交给 locale。
 */
function regional(locale: string): Writing {
  return { locale, month: 'short', day: 'numeric', stripComma: false };
}

/**
 * 按界面语言语种覆盖书写方式。中文是唯一例外：CLDR 的 zh 写法用斜杠，中文惯例与 GB/T 7408
 * 用短横线，因此改用同序带短横线的 en-CA 并固定 24 小时制。不要扩成 locale → pattern 表：
 * 手写 pattern 填错不报错，只会静默渲染错误日期。新增覆盖须说明依据。
 */
const WRITING_OVERRIDES: Record<string, Writing> = {
  zh: ISO_DASHED,
};

/**
 * 管道与需要同一写法的非模板调用方（如日期区间输入框）共用的渲染入口。
 * 两处各写一份，列表里的时间与筛选框里的日期迟早写成两种样子。
 */
export function formatAppDate(
  value: string | number | Date | null | undefined,
  format: AppDateFormat = 'full',
  timeZone?: string,
  locale?: string,
): string {
  if (value === null || value === undefined || value === '') {
    return '';
  }

  const date = value instanceof Date ? value : new Date(value);
  if (Number.isNaN(date.getTime())) {
    return '';
  }

  const writing = writingFor(locale);
  const options = optionsFor(format, writing);

  // 设置里存了当前运行时不认识的时区（时区数据库更名等）时退回浏览器时区，
  // 不要让一个偏好设置把整张列表打成空白；判定与按时区请求数据的地方共用
  return render(date, writing, options, resolveDisplayTimeZone(timeZone));
}

/**
 * 把 {@link formatAppDate} 的 `date` 槽位写法解析回浏览器本地的日历日，也接受 `YYYY-MM-DD`；
 * 认不出返回 `null`。不逐语种写解析规则：找出四位年份后把这一年每天按同一写法渲染比对
 * （忽略大小写、空白与标点），能显示的就能认回，认回的一定是存在的日期。
 */
export function parseAppCalendarDate(text: string, locale?: string): Date | null {
  const trimmed = text.trim();
  const iso = /^(\d{4})-(\d{2})-(\d{2})$/.exec(trimmed);
  if (iso) {
    return calendarDay(Number(iso[1]), Number(iso[2]), Number(iso[3]));
  }

  const year = /(?:^|\D)(\d{4})(?:\D|$)/.exec(trimmed);
  if (!year) {
    return null;
  }

  const target = comparable(trimmed);
  const day = new Date(Number(year[1]), 0, 1);
  while (day.getFullYear() === Number(year[1])) {
    if (comparable(formatAppDate(day, 'date', undefined, locale)) === target) {
      return new Date(day);
    }
    day.setDate(day.getDate() + 1);
  }
  return null;
}

/** 构造本地日历日并反查：`new Date(2026, 1, 31)` 不报错而是滚到 3 月，不反查就会收下不存在的日期。 */
function calendarDay(year: number, month: number, day: number): Date | null {
  const date = new Date(year, month - 1, day);
  return date.getFullYear() === year && date.getMonth() === month - 1 && date.getDate() === day
    ? date
    : null;
}

function comparable(text: string): string {
  return text.toLowerCase().replace(/[^\p{L}\p{N}]/gu, '');
}

// Intl.DateTimeFormat 构造不便宜，解析时一年要渲染三百多次，按参数缓存。
const formatters = new Map<string, Intl.DateTimeFormat>();

function render(
  date: Date,
  writing: Writing,
  options: Intl.DateTimeFormatOptions,
  timeZone?: string,
): string {
  const resolved: Intl.DateTimeFormatOptions = {
    ...options,
    ...(writing.hour12 === undefined ? {} : { hour12: writing.hour12 }),
    ...(timeZone ? { timeZone } : {}),
  };
  const key = `${writing.locale}|${JSON.stringify(resolved)}`;
  let formatter = formatters.get(key);
  if (!formatter) {
    formatter = new Intl.DateTimeFormat(writing.locale, resolved);
    formatters.set(key, formatter);
  }

  const formatted = formatter.format(date);
  return writing.stripComma ? formatted.replace(',', '') : formatted;
}

/** 认不出的 locale 回落到固定写法，而不是让整列变空。 */
function writingFor(locale?: string): Writing {
  if (!locale) {
    return ISO_DASHED;
  }

  try {
    return WRITING_OVERRIDES[new Intl.Locale(locale).language] ?? regional(locale);
  } catch {
    return ISO_DASHED;
  }
}

function optionsFor(format: AppDateFormat, writing: Writing): Intl.DateTimeFormatOptions {
  const slot = SLOTS[format] ?? SLOTS.full;
  const options: Intl.DateTimeFormatOptions = {};

  if (slot.date !== 'none') {
    if (slot.date === 'full') {
      options.year = 'numeric';
    }

    options.month = writing.month;
    options.day = writing.day;
  }

  if (slot.time) {
    options.hour = '2-digit';
    options.minute = '2-digit';
    if (slot.seconds) {
      options.second = '2-digit';
    }
  }

  return options;
}

/**
 * 按指定 IANA 时区渲染时刻。不用 Angular 的 `date` 管道：传 IANA 名会静默回落到浏览器时区，
 * 固定偏移也表达不了夏令时；`Intl.DateTimeFormat` 直接接受 IANA 名。
 *
 * `format` 是语义槽位，由调用点决定；`timeZone` 决定哪一刻，来自 `SettingContextService.timeZone`；
 * `locale` 决定怎么写，来自 `SettingContextService.displayLocale`（界面语言），不由时区推导：
 * 时区与地区不是一一对应（`Europe/Zurich` 有三种写法，`UTC` 没有国家）。两者由调用方传入，
 * 管道保持 pure。
 */
@Pipe({ name: 'appDate' })
export class AppDate implements PipeTransform {
  transform(
    value: string | number | Date | null | undefined,
    format: AppDateFormat = 'full',
    timeZone?: string,
    locale?: string,
  ): string {
    return formatAppDate(value, format, timeZone, locale);
  }
}
