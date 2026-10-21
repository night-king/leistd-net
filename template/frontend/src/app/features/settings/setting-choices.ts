//#if (IncludeLocalization)
import { LANG_OPTIONS } from '../../core/services/language-service';
//#endif
import { SETTINGS } from '../../core/settings/setting.constants';

/** 一个可选值及其展示文案。 */
export interface SettingChoice {
  value: string;
  label: string;
}

/**
 * 日志级别候选项，与后端 `SettingConstant.Logging.Levels`（Serilog `LogEventLevel`）逐字一致。
 * 不本地化：翻译后对不上日志里实际出现的词。
 */
const LOG_LEVEL_CHOICES: readonly SettingChoice[] = [
  { value: 'Verbose', label: 'Verbose' },
  { value: 'Debug', label: 'Debug' },
  { value: 'Information', label: 'Information' },
  { value: 'Warning', label: 'Warning' },
  { value: 'Error', label: 'Error' },
  { value: 'Fatal', label: 'Fatal' },
];

/** 日志级别说明的词条键，选中后显示：级别名说不出会多打多少日志。级别名不翻译，说明要翻译。 */
export const LOG_LEVEL_DESCRIPTIONS: Readonly<Record<string, string>> = {
  Verbose: 'settings.logLevelHints.Verbose',
  Debug: 'settings.logLevelHints.Debug',
  Information: 'settings.logLevelHints.Information',
  Warning: 'settings.logLevelHints.Warning',
  Error: 'settings.logLevelHints.Error',
  Fatal: 'settings.logLevelHints.Fatal',
};

/**
 * 取值封闭且选项不多的设置的候选项。候选项留在前端：后端设置定义不承载控件元数据，否则每加一种
 * 控件都要改后端契约。时区见 {@link timeZoneGroups}；其余设置渲染成文本框。
 */
export const SETTING_CHOICES: Readonly<Record<string, readonly SettingChoice[]>> = {
  //#if (IncludeLocalization)
  // 直接取语言服务的清单：候选值就是应用真正支持的语言，另抄一份必然漂移。
  // 文案用语言自身的本地名，因此不随界面语言变化，也不需要词条。
  [SETTINGS.display.language]: LANG_OPTIONS.map((o) => ({ value: o.id, label: o.label })),
  //#endif
  [SETTINGS.logging.minimumLevel]: LOG_LEVEL_CHOICES,
  [SETTINGS.logging.requestLevel]: LOG_LEVEL_CHOICES,
};

/** 时区选项：一个分组里的一行。 */
export interface TimeZoneOption {
  /** IANA 名，也是存进设置的值。 */
  value: string;
  /** 去掉地区前缀后的地名，下划线换成空格。 */
  city: string;
  /** 此刻的 UTC 偏移文案，如 `UTC+8`。 */
  offsetLabel: string;
  /** 同一偏移的 `GMT+8` 写法，只用于搜索匹配。 */
  offsetAlias: string;
  isBrowser: boolean;
  /** 搜索时额外匹配的名字（见 {@link SEARCH_ALIASES}）。 */
  aliases: readonly string[];
}

/** 按 IANA 名的地区前缀分组。 */
export interface TimeZoneGroup {
  region: string;
  zones: readonly TimeZoneOption[];
}

/** 已改名时区的搜索别名（如规范名 `Asia/Calcutta` 可搜 Kolkata）；只影响匹配，不另造选项。 */
const SEARCH_ALIASES: Record<string, readonly string[]> = {
  'Asia/Calcutta': ['Kolkata'],
  'Asia/Kolkata': ['Calcutta'],
  'Asia/Saigon': ['Ho Chi Minh'],
  'Asia/Ho_Chi_Minh': ['Saigon'],
  'Europe/Kiev': ['Kyiv'],
  'Europe/Kyiv': ['Kiev'],
  'Asia/Rangoon': ['Yangon'],
  'Asia/Yangon': ['Rangoon'],
  'America/Buenos_Aires': ['Argentina'],
};

/**
 * 该时区在当前运行时能否渲染，能则返回规范名。刻意构造一次 formatter 判定，不查
 * `Intl.supportedValuesOf('timeZone')`：它不含 `UTC`、`Asia/Kolkata` 等可用值。
 */
function canonicalTimeZone(timeZone: string): string | undefined {
  try {
    return new Intl.DateTimeFormat('en', { timeZone }).resolvedOptions().timeZone;
  } catch {
    return undefined;
  }
}

/** 该时区此刻的偏移文案（`GMT+8` 改写为 `UTC+8`，两种写法都参与搜索）；取不到返回空串。 */
function offsetLabel(timeZone: string): { label: string; alias: string } {
  try {
    const parts = new Intl.DateTimeFormat('en-US', {
      timeZone,
      timeZoneName: 'shortOffset',
    }).formatToParts(new Date());
    const alias = parts.find((p) => p.type === 'timeZoneName')?.value ?? '';
    return { label: alias.replace(/^GMT/, 'UTC'), alias };
  } catch {
    return { label: '', alias: '' };
  }
}

/** 该时区此刻的 UTC 偏移分钟数，用于组内排序；取不到按 0 处理。 */
function offsetMinutes(timeZone: string): number {
  try {
    const now = new Date();
    // 同一时刻在目标时区与 UTC 下的"墙上时间"之差即为偏移
    const local = new Date(now.toLocaleString('en-US', { timeZone }));
    const utc = new Date(now.toLocaleString('en-US', { timeZone: 'UTC' }));
    return Math.round((local.getTime() - utc.getTime()) / 60_000);
  } catch {
    return 0;
  }
}

/** 浏览器所在时区（IANA 名）。 */
export function browserTimeZone(): string | undefined {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || undefined;
  } catch {
    return undefined;
  }
}

/**
 * 全量时区，按地区分组：可搜索后全量不再是负担，精选清单必然漏掉部署地。额外补上 `UTC` 与
 * 浏览器时区，并按规范名去重。
 */
export function timeZoneGroups(): readonly TimeZoneGroup[] {
  const browser = browserTimeZone();
  const candidates: string[] = [];
  try {
    candidates.push(...Intl.supportedValuesOf('timeZone'));
  } catch {
    // 运行时没有这个 API：至少还能给出 UTC 与浏览器时区
  }

  // UTC 与浏览器时区都可能不在规范名清单里，显式补上；顺序在后，
  // 去重时会让规范名优先成为展示用的那一个。
  candidates.push('UTC');
  if (browser) {
    candidates.push(browser);
  }

  const chosen = new Map<string, string>();
  for (const id of candidates) {
    const canonical = canonicalTimeZone(id);
    if (canonical && !chosen.has(canonical)) {
      chosen.set(canonical, id);
    }
  }

  const browserCanonical = browser ? canonicalTimeZone(browser) : undefined;
  // 偏移先算好再排序：offsetMinutes 每次构造两个 formatter，放进比较器会乘上比较次数。
  const groups = new Map<string, { option: TimeZoneOption; offset: number }[]>();

  for (const [canonical, id] of chosen) {
    const separator = id.indexOf('/');
    // 没有地区前缀的（UTC、GMT 之类）单独归到一组，而不是塞进某个大洲
    const region = separator < 0 ? 'UTC' : id.slice(0, separator);
    const city = separator < 0 ? id : id.slice(separator + 1).replaceAll('_', ' ');

    const offset = offsetLabel(id);
    const zones = groups.get(region) ?? [];
    zones.push({
      option: {
        value: id,
        city,
        offsetLabel: offset.label,
        offsetAlias: offset.alias,
        isBrowser: canonical === browserCanonical,
        aliases: SEARCH_ALIASES[id] ?? [],
      },
      offset: offsetMinutes(id),
    });
    groups.set(region, zones);
  }

  return [...groups.entries()]
    .map(([region, zones]) => ({
      region,
      // 组内按偏移排：找时区多半是"我在 UTC+8"，同偏移再按地名稳定排序
      zones: zones
        .sort((a, b) => a.offset - b.offset || a.option.city.localeCompare(b.option.city))
        .map((entry) => entry.option),
    }))
    .sort((a, b) => a.region.localeCompare(b.region));
}

/** 搜索匹配：地名、完整 IANA 名、偏移（`UTC+8` 与 `GMT+8`）与别名都算命中。 */
export function timeZoneMatches(option: TimeZoneOption, search: string): boolean {
  const needle = search.trim().toLowerCase();
  if (needle.length === 0) {
    return true;
  }

  const haystack = [
    option.city,
    option.value,
    option.offsetLabel,
    option.offsetAlias,
    ...option.aliases,
  ];
  return haystack.some((text) => text.toLowerCase().includes(needle));
}
