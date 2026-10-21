import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideInfo } from '@ng-icons/lucide';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmTooltipImports } from '@spartan-ng/helm/tooltip';

/**
 * 标题、标签旁的补充说明：一个信息图标，鼠标悬停或键盘聚焦时用 Tooltip 显示说明，Escape 收起。
 *
 * Tooltip 按设计不响应触摸，这里只放可有可无的补充；触屏用户也必须看到的内容应写进正文。
 * 按钮在粗指针下按项目约定放大到 44px，负外边距让它不撑高所在的标题行。
 */
@Component({
  selector: 'app-info-hint',
  imports: [NgIcon, HlmButton, HlmTooltipImports],
  providers: [provideIcons({ lucideInfo })],
  templateUrl: './info-hint.html',
  // 行内原子盒：嵌在文字里不拆开所在段落；在 flex 行里随 items-center 居中
  host: { class: 'inline-flex align-middle' },
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class InfoHint {
  /** 说明文字，同时是按钮的可访问名。 */
  readonly text = input.required<string>();
}
