import { RowData, Table } from '@tanstack/angular-table';

import type { AppTableFeatures } from './table-features';

/** 切换某列排序：升序 → 降序 → 升序。 */
export function toggleTableSort<TData extends RowData>(
  table: Table<AppTableFeatures, TData>,
  columnId: string,
): void {
  const column = table.getColumn(columnId);
  column?.toggleSorting(column.getIsSorted() === 'asc');
}

/** 排序指示图标名（lucide）。 */
export function tableSortIcon<TData extends RowData>(
  table: Table<AppTableFeatures, TData>,
  columnId: string,
): string {
  const direction = table.getColumn(columnId)?.getIsSorted();
  return direction === 'asc'
    ? 'lucideSortAsc'
    : direction === 'desc'
      ? 'lucideSortDesc'
      : 'lucideArrowUpDown';
}

/** 表头 `aria-sort` 的取值；`null` 表示不渲染该属性。 */
export type TableSortAria = 'ascending' | 'descending' | null;

/**
 * 表头的 `aria-sort` 取值，与图标同源，免得视觉与读屏说出不同方向。只有当前排序的列带这个属性
 * （WAI-ARIA 建议一张表同一时刻只给一个表头）；未排序与不可排序的列返回 `null`，`[attr.aria-sort]` 据此不渲染。
 * 能否排序由排序按钮表达，不靠 `aria-sort="none"`。
 */
export function tableSortAria<TData extends RowData>(
  table: Table<AppTableFeatures, TData>,
  columnId: string,
): TableSortAria {
  const direction = table.getColumn(columnId)?.getIsSorted();
  return direction === 'asc' ? 'ascending' : direction === 'desc' ? 'descending' : null;
}
