import { Directive } from '@angular/core';
import { classes } from '@spartan-ng/helm/utils';

/**
 * 状态徽章前的圆点：挂在 `hlmBadge` 上，用 `::before` 画一个当前文字色的小圆点，不插 DOM、不另建状态映射，
 * 颜色随徽章变体走。只用于状态（启用、已锁定、成功、被拒绝、当前设备等），角色名这类分类标签不加。
 *
 * ```html
 * <span hlmBadge variant="success" appStatusDot>{{ statusText }}</span>
 * ```
 */
@Directive({ selector: '[appStatusDot]' })
export class StatusDot {
  constructor() {
    classes(
      () =>
        "before:size-1.5 before:shrink-0 before:rounded-full before:bg-current before:content-['']",
    );
  }
}
