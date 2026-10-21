export type DeviceKind = 'desktop' | 'mobile' | 'tablet';

/** 从 User-Agent 归纳出的设备描述；认不出的部分为 null，由界面写成"未知"。 */
export interface DeviceDescription {
  /** 浏览器名与主版本，如 `Chrome 128`。 */
  browser: string | null;
  /** 操作系统名，如 `macOS`、`Windows`。 */
  os: string | null;
  kind: DeviceKind;
}

/**
 * 按顺序匹配：基于 Chromium 的浏览器都带 `Chrome/`，Chrome 本身必须排在它们后面；
 * Safari 同理排在所有带 `Safari/` 的浏览器之后。
 */
const BROWSERS: readonly (readonly [string, RegExp])[] = [
  ['Edge', /Edg(?:e|A|iOS)?\/(\d+)/],
  ['Opera', /(?:OPR|Opera)\/(\d+)/],
  ['Samsung Internet', /SamsungBrowser\/(\d+)/],
  ['Firefox', /(?:Firefox|FxiOS)\/(\d+)/],
  ['Chrome', /(?:Chrome|CriOS)\/(\d+)/],
  ['Safari', /Version\/(\d+)[.\d]* (?:Mobile\/\S+ )?Safari\//],
];

/** iPad 在 iPadOS 13 之后默认报成 Mac，那种情况认不出来，按桌面处理。 */
const OPERATING_SYSTEMS: readonly (readonly [string, RegExp])[] = [
  ['iOS', /iPhone|iPad|iPod/],
  ['Android', /Android/],
  ['Windows', /Windows/],
  ['ChromeOS', /CrOS/],
  ['macOS', /Macintosh|Mac OS X/],
  ['Linux', /Linux/],
];

/**
 * 把 User-Agent 归纳成"浏览器 · 系统"，供用户认出设备。在界面上归纳而不是存库时归纳，
 * 规则改了旧记录也跟着变对；只认主流浏览器与系统，认不出时显示"未知"。
 */
export function describeUserAgent(userAgent: string | null | undefined): DeviceDescription {
  const ua = userAgent ?? '';
  const browserMatch = BROWSERS.map(([name, pattern]) => [name, pattern.exec(ua)] as const).find(
    ([, match]) => match,
  );
  const os = OPERATING_SYSTEMS.find(([, pattern]) => pattern.test(ua))?.[0] ?? null;

  const kind: DeviceKind =
    /iPad|Tablet/.test(ua) || (/Android/.test(ua) && !/Mobile/.test(ua))
      ? 'tablet'
      : /Mobi|iPhone|iPod/.test(ua)
        ? 'mobile'
        : 'desktop';

  return {
    browser: browserMatch ? `${browserMatch[0]} ${browserMatch[1]![1]}` : null,
    os,
    kind,
  };
}
