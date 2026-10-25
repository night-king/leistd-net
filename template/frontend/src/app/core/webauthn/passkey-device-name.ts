import { describeUserAgent } from '../../shared/utils/user-agent';

/** `navigator.userAgentData` 里用得到的那部分（还不在 TypeScript 的 DOM 类型里）。 */
export interface UserAgentDataLike {
  readonly platform?: string;
  readonly brands?: readonly { readonly brand: string }[];
}

/** 名称最长 128 个字符，与服务端 `RenamePasskeyInputDto` 一致。 */
export const PASSKEY_NAME_MAX_LENGTH = 128;

const BRAND_NAMES: Readonly<Record<string, string>> = {
  'Google Chrome': 'Chrome',
  'Microsoft Edge': 'Edge',
  Opera: 'Opera',
  Brave: 'Brave',
  Chromium: 'Chromium',
};

/**
 * 通行密钥的默认名称，如 `macOS · Chrome`，便于在列表里认出是哪台设备上登记的；一样都认不出时返回 `null`，
 * 由调用方回落到本地化的"当前设备"。
 *
 * 优先读 `userAgentData`：结构化的平台与品牌，不必猜字符串；品牌列表里混有故意伪造的占位项（"Not.A/Brand"
 * 一类），要剔除，`Chromium` 只在没有更具体的品牌时才用。读不到时回落到 User-Agent，与登录设备列表同一套归纳，
 * 只是不带版本号：名称会留很久，版本号很快就不对了。
 */
export function describePasskeyDevice(
  userAgentData: UserAgentDataLike | null | undefined,
  userAgent: string | null | undefined,
): string | null {
  const fromUserAgent = describeUserAgent(userAgent);
  const parts = [
    platformFrom(userAgentData?.platform) ?? fromUserAgent.os,
    browserFromBrands(userAgentData?.brands) ??
      fromUserAgent.browser?.replace(/\s+\d+$/, '') ??
      null,
  ].filter((part): part is string => !!part);

  return parts.length ? parts.join(' · ').slice(0, PASSKEY_NAME_MAX_LENGTH) : null;
}

/** 当前浏览器的默认名称；认不出时为 `null`。 */
export function currentPasskeyDeviceName(): string | null {
  if (typeof navigator === 'undefined') {
    return null;
  }
  const userAgentData = (navigator as Navigator & { userAgentData?: UserAgentDataLike })
    .userAgentData;
  return describePasskeyDevice(userAgentData, navigator.userAgent);
}

function platformFrom(platform: string | undefined): string | null {
  const value = platform?.trim();
  if (!value || value.toLowerCase() === 'unknown') {
    return null;
  }
  return value === 'Chrome OS' ? 'ChromeOS' : value;
}

function browserFromBrands(
  brands: readonly { readonly brand: string }[] | undefined,
): string | null {
  const known = (brands ?? [])
    .map((item) => BRAND_NAMES[item.brand])
    .filter((brand): brand is string => !!brand);
  return known.find((brand) => brand !== 'Chromium') ?? known[0] ?? null;
}
