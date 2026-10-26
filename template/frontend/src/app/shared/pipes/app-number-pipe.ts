import { Pipe, PipeTransform } from '@angular/core';

/**
 * 语义槽位：调用点按用途声明数字怎么写。与 `appDate` 同一思路——精度与写法属于槽位，
 * 分组符号、紧凑单位（k / 万）交给界面语言的 locale。
 * - `integer`：带千分位的整数，计数与总数。
 * - `compact`：紧凑写法（`1.2k`、`1.2万`），指标卡大数与坐标轴刻度；完整整数由调用方放进提示。
 * - `change`：变化率，输入是**比例**（0.29 表示 +29%），带正负号，0 写作 `0%`。
 */
const SLOTS = {
  integer: { maximumFractionDigits: 0 },
  compact: { notation: 'compact', maximumFractionDigits: 1 },
  change: { style: 'percent', signDisplay: 'exceptZero', maximumFractionDigits: 0 },
} as const satisfies Record<string, Intl.NumberFormatOptions>;

export type AppNumberFormat = keyof typeof SLOTS;

/** 拿不到或认不出界面语言时的回落：固定写法，不随运行环境漂移。 */
const FALLBACK_LOCALE = 'en-US';

// Intl.NumberFormat 构造不便宜，表格每格、坐标轴每个刻度都会调用，按参数缓存。
const formatters = new Map<string, Intl.NumberFormat>();

/**
 * 管道与需要同一写法的非模板调用方（图表刻度、词条参数）共用的渲染入口。
 * `null` / `undefined` / 非有限数渲染为空串，由调用方决定是否显示占位。
 */
export function formatAppNumber(
  value: number | null | undefined,
  format: AppNumberFormat = 'integer',
  locale?: string,
): string {
  if (value === null || value === undefined || !Number.isFinite(value)) {
    return '';
  }
  return formatterFor(format, locale).format(value);
}

function formatterFor(format: AppNumberFormat, locale?: string): Intl.NumberFormat {
  const resolved = supported(locale) ? locale : FALLBACK_LOCALE;
  const key = `${resolved}|${format}`;
  let formatter = formatters.get(key);
  if (!formatter) {
    formatter = new Intl.NumberFormat(resolved, SLOTS[format] ?? SLOTS.integer);
    formatters.set(key, formatter);
  }
  return formatter;
}

function supported(locale: string | undefined): locale is string {
  if (!locale) {
    return false;
  }
  try {
    return Intl.NumberFormat.supportedLocalesOf(locale).length > 0;
  } catch {
    return false;
  }
}

/**
 * 按界面语言书写数字。`locale` 来自 `AccountPreferencesService.displayLocale`，由调用方传入，管道保持 pure。
 *
 * ```html
 * {{ total | appNumber: 'integer' : displayLocale() }}
 * ```
 */
@Pipe({ name: 'appNumber' })
export class AppNumber implements PipeTransform {
  transform(
    value: number | null | undefined,
    format: AppNumberFormat = 'integer',
    locale?: string,
  ): string {
    return formatAppNumber(value, format, locale);
  }
}
