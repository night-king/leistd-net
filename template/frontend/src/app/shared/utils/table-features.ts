import {
  columnVisibilityFeature,
  createTableHook,
  metaHelper,
  rowExpandingFeature,
  rowPaginationFeature,
  rowSelectionFeature,
  rowSortingFeature,
  tableFeatures,
} from '@tanstack/angular-table';

export type TableColumnPriority = 'primary' | 'secondary' | 'tertiary';

/** 应用表格的列元数据，通过 feature 类型槽限定可用属性。 */
export interface AppColumnMeta {
  locked?: boolean;
  priority: TableColumnPriority;
  /**
   * 数据列被折叠隐藏时进入行展开详情（见 `hiddenDetailColumns`）。选择、操作这类非数据列不登记；
   * 始终显示的列登记了也不会出现。
   */
  detail?: boolean;
  headClass?: string;
  cellClass?: string;
}

/**
 * 平台表格共用的能力。排序与分页由服务端完成，不加载客户端排序或分页行模型。
 * 行展开只用它的状态（`row.getIsExpanded()` / `row.toggleExpanded()`）补偿被响应式隐藏的列，
 * 没有子行，因此也不加载展开行模型。行选择只在需要批量操作的表格里接上状态（用户表），
 * 选择只在当前页内有效。
 */
const features = tableFeatures({
  columnVisibilityFeature,
  rowExpandingFeature,
  rowPaginationFeature,
  rowSelectionFeature,
  rowSortingFeature,
  columnMeta: metaHelper<AppColumnMeta>(),
});

/**
 * 预绑定应用 feature 集合的表格 hook。行展开改两项默认值：每行都可展开（内容是被隐藏的列），
 * 换数据不收起（按行 id 记）；各表格须用 `getRowId` 给出实体 id。
 */
export const { injectAppTable } = createTableHook({
  features,
  getRowCanExpand: () => true,
  autoResetExpanded: false,
});

/** 供表格与列定义使用的应用 feature 类型。 */
export type AppTableFeatures = typeof features;
