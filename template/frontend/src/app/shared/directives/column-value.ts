import { Directive, input } from '@angular/core';

/** 列值模板的形态：`cell` 是表格单元格里的摘要，`detail` 是行展开详情里的完整内容。 */
export type ColumnValueMode = 'cell' | 'detail';

/** 列值模板的上下文：行数据、列 id 与形态。 */
export interface ColumnValueContext<TRow> {
  readonly $implicit: TRow;
  readonly id: string;
  readonly mode: ColumnValueMode;
}

/**
 * 给表格的列值模板标上行类型：单元格与展开详情经 `ngTemplateOutlet` 共用同一个模板，按 `mode` 给摘要或完整内容。
 * 只为模板类型检查服务，传入的行数组不被读取。
 *
 * ```html
 * <ng-template #columnValue [appColumnValue]="users()" let-user let-id="id" let-mode="mode">
 *   @switch (id) { @case ('email') { … } }
 * </ng-template>
 * ```
 */
@Directive({ selector: 'ng-template[appColumnValue]' })
export class ColumnValue<TRow> {
  readonly rows = input.required<readonly TRow[]>({ alias: 'appColumnValue' });

  static ngTemplateContextGuard<TRow>(
    _directive: ColumnValue<TRow>,
    _context: unknown,
  ): _context is ColumnValueContext<TRow> {
    return true;
  }
}
