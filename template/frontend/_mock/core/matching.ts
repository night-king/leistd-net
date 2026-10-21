import { MockConfig } from './models';

// Mock 的两条判定规则，只有这一份：是否安装 Mock、某个请求是否走 Mock。
// 不引用 Mock 数据与接口定义，被引用时不会把它们带进包里。

/** 是否需要安装 Mock：开启总开关，或列出了要 Mock 的接口。 */
export function shouldProvideMock(config: boolean | MockConfig): boolean {
  return typeof config === 'boolean' ? config : config.enable || hasPatterns(config.include);
}

/** 这个请求是否走 Mock：列了 include 时以它为准，否则看总开关；命中 exclude 的一律不走。 */
export function isMockedUrl(config: boolean | MockConfig, url: string): boolean {
  const normalized = typeof config === 'boolean' ? { enable: config } : config;
  const urlPath = getUrlPath(url);
  const included = hasPatterns(normalized.include)
    ? matchesPatterns(urlPath, normalized.include!)
    : normalized.enable;
  const excluded = hasPatterns(normalized.exclude) && matchesPatterns(urlPath, normalized.exclude!);
  return included && !excluded;
}

export function getUrlPath(url: string): string {
  const urlWithoutQuery = url.split('?')[0];

  try {
    return new URL(urlWithoutQuery).pathname;
  } catch {
    return urlWithoutQuery;
  }
}

function hasPatterns(patterns?: string | string[]): boolean {
  return Array.isArray(patterns) ? patterns.length > 0 : Boolean(patterns);
}

function matchesPatterns(urlPath: string, patterns: string | string[]): boolean {
  const normalizedPatterns = Array.isArray(patterns) ? patterns : [patterns];
  return normalizedPatterns.some((pattern) => new RegExp(pattern).test(urlPath));
}
