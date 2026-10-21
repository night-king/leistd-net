/**
 * 断言表头的 `aria-sort` 只出现在当前排序的列上：没有排序时一个都没有；有排序时恰好一个，方向正确，
 * 并且是带排序按钮的表头格（排序按钮是表头格的直接子按钮）。返回带属性的表头格，便于换列后比对。
 */
export function expectAriaSortOnSortedHeaderOnly(
  host: HTMLElement,
  expected: 'ascending' | 'descending' | null,
): HTMLTableCellElement | null {
  const marked = [...host.querySelectorAll<HTMLTableCellElement>('thead th[aria-sort]')];
  if (expected === null) {
    expect(marked).toEqual([]);
    return null;
  }

  expect(marked).toHaveLength(1);
  const [header] = marked;
  expect(header.getAttribute('aria-sort')).toBe(expected);
  expect(header.querySelector(':scope > button')).not.toBeNull();
  return header;
}
