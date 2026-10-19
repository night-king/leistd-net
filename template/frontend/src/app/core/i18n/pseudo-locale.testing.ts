/**
 * 伪语言：把英文词条换成可辨认、更宽的写法，用来在单测里发现两类问题——固定宽度区域装不下更长的语言，
 * 以及没走词条的写死文案（渲染出来没有方括号）。
 *
 * 宽度按标签容量 max(⌈英文近似列宽 × 1.3⌉, 14 列) 生成（含首尾方括号，规则见 docs/standards/frontend-i18n.md §5）。
 * 这是模板选定的测试预算，不是像素容量：列数不等于比例字体的字形宽度，通过不代表任何语言都放得下。
 */

/** Transloco 插值 `{{name}}`：原样保留，不计入静态宽度（宽度由运行时的值决定）。 */
const INTERPOLATION = /\{\{[^}]+\}\}/g;
const PLAIN = 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ';
const ACCENTED = [...'àƀçđéƒĝĥîĵķľɱñöþǫŕšţûṽŵẋýžÀƁÇĐÉƑĜĤÎĴĶĽṀÑÖÞǪŔŠŢÛṼŴẊÝŽ'];
/** 东亚宽字符（CJK、假名、谚文、全角）：Unicode East Asian Width 为 W 或 F 的主要区段。 */
const WIDE =
  /[\u1100-\u115F\u2E80-\u303E\u3041-\u33FF\u3400-\u4DBF\u4E00-\u9FFF\uA960-\uA97F\uAC00-\uD7A3\uF900-\uFAFF\uFE30-\uFE4F\uFF00-\uFF60\uFFE0-\uFFE6]/u;
/** 不占列的字符：组合附加符号（泰文元音、越南文声调）与格式字符。 */
const ZERO_WIDTH = /[\p{Mn}\p{Me}\p{Cf}]/u;

const RATIO = 1.3;
const FLOOR = 14;

/** 近似显示宽度（列）：宽字符 2 列，组合符号与格式字符 0 列，其余 1 列；插值不计。 */
export function displayWidth(text: string): number {
  let width = 0;
  for (const char of text.replace(INTERPOLATION, '')) {
    if (ZERO_WIDTH.test(char)) continue;
    width += WIDE.test(char) ? 2 : 1;
  }
  return width;
}

/** 标签容量：max(⌈英文近似列宽 × 1.3⌉, 14 列)。 */
export function labelCapacity(source: string): number {
  return Math.max(FLOOR, Math.ceil(displayWidth(source) * RATIO));
}

/** 字母换成带附加符号的同形字，用 `~` 补到标签容量，首尾加 `[` `]`；插值原样保留。 */
export function pseudoLocalize(source: string): string {
  let body = '';
  let last = 0;
  for (const match of source.matchAll(INTERPOLATION)) {
    body += accent(source.slice(last, match.index)) + match[0];
    last = match.index + match[0].length;
  }
  body += accent(source.slice(last));
  const padding = Math.max(0, labelCapacity(source) - displayWidth(body) - 2);
  return `[${body}${'~'.repeat(padding)}]`;
}

/** 整棵词条树逐值伪语言化，结构不变；用法：`transloco.setTranslation(pseudoCatalog(enTexts), 'en')`。 */
export function pseudoCatalog<T>(node: T): T {
  if (typeof node === 'string') return pseudoLocalize(node) as T;
  if (!node || typeof node !== 'object') return node;
  return Object.fromEntries(
    Object.entries(node as Record<string, unknown>).map(([key, value]) => [
      key,
      pseudoCatalog(value),
    ]),
  ) as T;
}

/**
 * 元素里的文字实际排成几行：按文字行矩形的顶边计数。只看盒子尺寸量不出折行——
 * 折行的文字同样满足 `scrollWidth <= clientWidth`，多项一起折行时盒高也彼此相等。
 */
export function lineCount(element: Element): number {
  const range = element.ownerDocument.createRange();
  range.selectNodeContents(element);
  const tops = Array.from(range.getClientRects())
    .filter((rect) => rect.width > 0 && rect.height > 0)
    .map((rect) => rect.top)
    .sort((a, b) => a - b);
  // 同一行里不同字号的内联盒顶边相差几像素；行高至少 16px，顶边相差超过 6px 才算新的一行
  let lines = 0;
  let last = Number.NEGATIVE_INFINITY;
  for (const top of tops) {
    if (top - last > 6) {
      lines++;
      last = top;
    }
  }
  return lines;
}

function accent(text: string): string {
  return [...text].map((char) => ACCENTED[PLAIN.indexOf(char)] ?? char).join('');
}
