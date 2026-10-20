import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
//#if (IncludeLocalization)
import { TranslocoDirective } from '@jsverse/transloco';
//#endif
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideChevronRight, lucideTrash2 } from '@ng-icons/lucide';

import { OverflowProbe } from '../../directives/overflow-probe';
import { NotificationOutputDto } from '../../dtos/notification.dto';
//#if (!IncludeLocalization)
import { englishText } from '../../utils/english-text';
//#endif

/**
 * 通知列表的一行，铃铛面板与工作台共用：类型图标放浅色圆底、与标题顶部对齐；标题一行，未读圆点在标题右侧；
 * 正文最多两行；最下面一行左边是时间，右边是展开入口（被截断才有，「展开全文 ›」展开后转向下）。
 * 整行由铺满的主按钮响应（`opened`），删除、时间与展开叠在它上面，交互元素不嵌套。
 * 时间由使用方投影（`<app-relative-time-label notificationTime>`），类型图标由使用方注册。
 * 可删除的列表开 `removable`：悬停或聚焦一行时，删除按钮占到标题右侧未读圆点的位置。
 */
@Component({
  selector: 'app-notification-item',
  standalone: true,
  // prettier-ignore
  imports: [
    NgIcon,
    OverflowProbe,
    //#if (IncludeLocalization)
    TranslocoDirective,
    //#endif
  ],
  providers: [provideIcons({ lucideChevronRight, lucideTrash2 })],
  templateUrl: './notification-item.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class NotificationItem {
  readonly notification = input.required<NotificationOutputDto>();
  /** 类型图标名，由使用方按通知类型给出。 */
  readonly icon = input.required<string>();
  readonly expanded = input(false);
  /** 是否给出删除（`removed`）。 */
  readonly removable = input(false);

  /** 点了这一行（标已读、跳转由使用方决定）。 */
  readonly opened = output<void>();
  readonly expandedChange = output<boolean>();
  readonly removed = output<void>();

  protected readonly summaryId = computed(() => `notification-${this.notification().id}`);
  protected readonly titleId = computed(() => `${this.summaryId()}-title`);
  protected readonly contentId = computed(() => `${this.summaryId()}-content`);
  //#if (!IncludeLocalization)
  protected readonly t = englishText(ENGLISH);
  //#endif
}
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'common.showFullText': 'Show full text',
  'common.showLess': 'Show less',
  'common.remove': 'Remove',
};
//#endif
