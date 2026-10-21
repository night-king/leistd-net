import { Translation } from '@jsverse/transloco';

/**
 * 从 `translateObjectSignal` 取到的词条对象里按点号路径取一句文案，不存在时为 `undefined`。Transloco
 * 会把带点号的键展开成嵌套对象，因此按段逐级取；词条未到达时同样为 `undefined`，调用方据此退回原始标识。
 */
export function textAt(texts: Translation, path: string): string | undefined {
  let node: unknown = texts;
  for (const segment of path.split('.')) {
    if (node === null || typeof node !== 'object') {
      return undefined;
    }
    node = (node as Record<string, unknown>)[segment];
  }
  return typeof node === 'string' ? node : undefined;
}
