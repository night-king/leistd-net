import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
//#if (IncludeLocalization)
import { TranslocoDirective, TranslocoService } from '@jsverse/transloco';
//#endif
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideBell, lucideCircleAlert, lucideInbox, lucideTrash2 } from '@ng-icons/lucide';
import { toast } from '@spartan-ng/brain/sonner';
import { HlmBadge } from '@spartan-ng/helm/badge';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmPopoverImports } from '@spartan-ng/helm/popover';
import { HlmSpinner } from '@spartan-ng/helm/spinner';
import { HlmTooltipImports } from '@spartan-ng/helm/tooltip';

import { applicationErrorMessage } from '../../../core/errors/application-http-error';
import { ConfirmService } from '../../../core/feedback/confirm-service';
import {
  NOTIFICATION_ICONS,
  NotificationService,
} from '../../../core/services/notification-service';
import { SettingContextService } from '../../../core/settings/setting-context-service';
import { PopoverAria } from '../../../shared/directives/popover-aria';
import { NotificationOutputDto } from '../../../shared/dtos/notification.dto';
import { AppDate } from '../../../shared/pipes/app-date-pipe';
//#if (!IncludeLocalization)
import { englishText } from '../../../shared/utils/english-text';
//#endif

/** 通知中心：铃铛 + 未读角标 + popover 通知列表；仅在启用通知功能时生成（见 template.json 排除规则）。 */
@Component({
  selector: 'app-notifications',
  standalone: true,
  host: { class: 'inline-flex' },
  // prettier-ignore
  imports: [
    NgIcon,
    HlmButton,
    HlmBadge,
    HlmSpinner,
    AppDate,
    ...HlmPopoverImports,
    ...HlmTooltipImports,
    PopoverAria,
    //#if (IncludeLocalization)
    TranslocoDirective,
    //#endif
  ],
  providers: [
    provideIcons({
      ...NOTIFICATION_ICONS,
      lucideBell,
      lucideCircleAlert,
      lucideInbox,
      lucideTrash2,
    }),
  ],
  templateUrl: './notifications.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Notifications implements OnInit {
  private readonly router = inject(Router);
  // 统一按展示时区渲染，避免同一时刻在不同页面显示成不同时间。
  protected readonly displayTimeZone = inject(SettingContextService).timeZone;
  protected readonly displayLocale = inject(SettingContextService).displayLocale;
  private readonly confirmService = inject(ConfirmService);
  //#if (IncludeLocalization)
  private readonly transloco = inject(TranslocoService);
  //#else
  protected readonly t = englishText(ENGLISH);
  //#endif
  readonly notificationService = inject(NotificationService);
  readonly notificationCount = this.notificationService.unreadCount;
  readonly notifications = this.notificationService.notifications;
  // 通知 popover 开合状态（Spartan popover 的 state 受控绑定）。
  readonly notificationOpen = signal<'open' | 'closed'>('closed');

  async onNotificationClick(item: NotificationOutputDto): Promise<void> {
    if (!item.isRead) {
      try {
        await this.notificationService.markAsRead(item.id);
      } catch (error: unknown) {
        // 标记已读是附带动作：失败只提示，不阻断跳转这一主动作。
        this.showRequestError(error);
      }
    }
    if (item.link) {
      this.notificationOpen.set('closed');
      this.router.navigateByUrl(item.link);
    }
  }

  async markAllNotificationsRead(): Promise<void> {
    try {
      await this.notificationService.markAllAsRead();
    } catch (error: unknown) {
      this.showRequestError(error);
    }
  }

  async clearAllNotifications(): Promise<void> {
    // 清空不可撤销，先确认。
    const confirmed = await this.confirmService.open({
      //#if (IncludeLocalization)
      message: this.transloco.translate('layout.notifications.clearAllConfirm'),
      header: this.transloco.translate('layout.notifications.clearAll'),
      confirmText: this.transloco.translate('common.ok'),
      cancelText: this.transloco.translate('common.cancel'),
      //#else
      message: 'Clear all notifications? This cannot be undone.',
      header: 'Clear all',
      confirmText: 'OK',
      cancelText: 'Cancel',
      //#endif
      variant: 'destructive',
    });
    if (!confirmed) {
      return;
    }
    try {
      await this.notificationService.clearAll();
    } catch (error: unknown) {
      // 删除失败：保持面板打开并提示，避免「看起来成功、重开还在」。
      this.showRequestError(error);
      return;
    }
    this.notificationOpen.set('closed');
  }

  async clearOneNotification(id: string): Promise<void> {
    try {
      await this.notificationService.clearOne(id);
    } catch (error: unknown) {
      this.showRequestError(error);
    }
  }

  private showRequestError(error: unknown): void {
    //#if (IncludeLocalization)
    toast.error(this.transloco.translate('common.requestError'), {
      description: applicationErrorMessage(error),
    });
    //#else
    toast.error('Request failed', { description: applicationErrorMessage(error) });
    //#endif
  }

  /** 重新加载历史通知：只重取列表，不重建实时连接。 */
  retryLoad(): void {
    void this.notificationService.loadNotifications();
  }

  notificationIcon(type: string): string {
    return this.notificationService.getIcon(type);
  }

  ngOnInit(): void {
    void this.notificationService.init();
  }
}
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'layout.notifications.unreadAria': 'Notifications ({{count}} unread)',
  'layout.notifications.title': 'Notifications',
  'layout.notifications.markAllRead': 'Mark all as read',
  'layout.notifications.empty': 'No notifications',
  'layout.notifications.loading': 'Loading notifications…',
  'layout.notifications.loadFailed': "Couldn't load notifications.",
  'layout.notifications.historyStale': "Couldn't refresh earlier notifications.",
  'common.retry': 'Retry',
  'layout.notifications.clearOne': 'Dismiss',
  'layout.notifications.clearAll': 'Clear all',
};
//#endif
