import type { AppColumnMeta, AppTableFeatures } from './table-features';
import type {
  Column,
  ColumnDef,
  ColumnVisibilityState,
  RowData,
  Table,
} from '@tanstack/table-core';

export type TableViewport = 'mobile' | 'tablet' | 'desktop';

/** 行操作列的列元数据：右侧吸附、不参与排序与隐藏；四个平台表格共用，避免样式各自漂移。 */
export const ACTIONS_COLUMN_META: AppColumnMeta = {
  priority: 'primary',
  locked: true,
  headClass:
    'w-px px-2 text-right whitespace-nowrap sticky right-0 z-20 bg-muted border-l border-border',
  cellClass:
    'w-px px-2 py-2 whitespace-nowrap sticky right-0 z-10 bg-card group-hover:bg-muted/50 border-l border-border',
};

/**
 * 行的主列（名称、标识）的列元数据：始终显示，不参与隐藏。到最窄一档（`data-table-fit="mobile"`）
 * 主列占满剩余宽度，内容包一层 `TITLE_CONTENT_CLASS` 截断长名称，免得吸附的操作列压住其他列；
 * 更宽的档位不截断，否则 `TableFit` 量不到溢出。
 */
export const TITLE_COLUMN_META: AppColumnMeta = {
  priority: 'primary',
  locked: true,
  cellClass: 'first:ps-4 in-data-[table-fit=mobile]:w-full',
};

/**
 * 主列单元格内容的外层 class：最窄一档下不按内容撑宽（行内尺寸包含），宽度由单元格给定，
 * 里面的文字用 `truncate` 截断。
 */
export const TITLE_CONTENT_CLASS = 'min-w-0 in-data-[table-fit=mobile]:[contain:inline-size]';

export function tableColumnVisibility<TData extends RowData>(
  columns: readonly ColumnDef<AppTableFeatures, TData>[],
  viewport: TableViewport,
): ColumnVisibilityState {
  return Object.fromEntries(
    columns.flatMap((column) => {
      const id = 'id' in column ? column.id : undefined;
      if (!id) {
        return [];
      }

      const meta = column.meta;
      const visible =
        meta?.locked === true ||
        meta?.priority === undefined ||
        meta.priority === 'primary' ||
        (meta.priority === 'secondary' && viewport !== 'mobile') ||
        (meta.priority === 'tertiary' && viewport === 'desktop');

      return [[id, visible]];
    }),
  );
}

/**
 * 行展开详情要列出的列：登记了 `meta.detail` 且当前被折叠隐藏的列，按列定义顺序。
 * 详情的标题与表头取同一份标签，值与单元格取同一个值模板（详情形态给完整内容）。
 */
export function hiddenDetailColumns<TData extends RowData>(
  table: Table<AppTableFeatures, TData>,
): Column<AppTableFeatures, TData, unknown>[] {
  return table
    .getAllLeafColumns()
    .filter((column) => column.columnDef.meta?.detail === true && !column.getIsVisible());
}
