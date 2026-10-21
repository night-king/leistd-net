import { MockException } from './models';

type SortValue = string | number | boolean | null | undefined;
export type MockSortFields<T> = Record<string, (item: T) => SortValue>;

/** Mock 仅支持界面使用的属性路径与方向，不模拟完整 Dynamic LINQ。 */
export function sortMockRows<T>(
  rows: T[],
  sorting: unknown,
  fields: MockSortFields<T>,
  defaultSorting: string,
  tieBreakers: string,
  nulls: 'first' | 'last' = 'last',
): T[] {
  const input = Array.isArray(sorting) ? sorting[0] : sorting;
  const text = String(input ?? '').trim() || defaultSorting;
  const keys = [...parse(text), ...parse(tieBreakers)];

  function parse(expression: string) {
    return expression.split(',').map((part) => {
      const match = /^([a-z_]\w*(?:\.[a-z_]\w*)*)(?:\s+(asc|ascending|desc|descending))?$/i.exec(
        part.trim(),
      );
      const path = match?.[1].toLowerCase();
      const field = Object.keys(fields).find((key) => key.toLowerCase() === path);
      // 与后端解析失败同形：sorting 字段的 400 验证错误，不带业务码
      if (!match || !field) {
        throw new MockException(400, {
          errors: [{ field: 'sorting', detail: 'The sorting expression is not valid.' }],
        });
      }
      return { select: fields[field], descending: match[2]?.toLowerCase().startsWith('desc') };
    });
  }

  return [...rows].sort((left, right) => {
    for (const key of keys) {
      const a = key.select(left);
      const b = key.select(right);
      let compared: number;
      if (a == null || b == null) {
        compared = a == null && b == null ? 0 : (a == null ? -1 : 1) * (nulls === 'first' ? 1 : -1);
      } else if (typeof a === 'string' && typeof b === 'string') {
        compared = a.localeCompare(b);
      } else {
        compared = Number(a) - Number(b);
      }
      if (compared !== 0) {
        return compared * (key.descending ? -1 : 1);
      }
    }
    return 0;
  });
}
