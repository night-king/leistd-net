import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/**
 * 托盘卡：灰底外框放标题与右侧操作（投影带 `statPanelAction` 属性的元素），内层白卡放内容。
 * 仪表盘这类同屏多块数据用它分组；普通页面区块仍用 Spartan Card。圆角按嵌套规则取：外框
 * `rounded-xl`，内卡 `rounded-lg`（外圆角减去 4px 间距）。
 */
@Component({
  selector: 'app-stat-panel',
  standalone: true,
  host: { class: 'bg-muted flex min-w-0 flex-col gap-1 rounded-xl p-1', 'data-slot': 'stat-panel' },
  template: `
    <div class="flex min-h-8 items-center gap-2 px-3 py-1">
      <h3 class="text-muted-foreground truncate text-sm font-medium">{{ panelTitle() }}</h3>
      <div class="ms-auto flex items-center gap-2">
        <ng-content select="[statPanelAction]" />
      </div>
    </div>
    <div class="bg-card text-card-foreground ring-foreground/10 flex-1 rounded-lg shadow-xs ring-1">
      <ng-content />
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class StatPanel {
  /** 外框标题。 */
  readonly panelTitle = input.required<string>();
}
