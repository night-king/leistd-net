import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Router } from '@angular/router';
//#if (IncludeLocalization)
import { TranslocoDirective, TranslocoService } from '@jsverse/transloco';
//#endif
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideBell, lucideCircleAlert, lucideInbox } from '@ng-icons/lucide';
import { toast } from '@spartan-ng/brain/sonner';
import { HlmBadge } from '@spartan-ng/helm/badge';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmPopoverImports } from '@spartan-ng/helm/popover';
import { HlmSpinner } from '@spartan-ng/helm/spinner';
import { interval } from 'rxjs';

import { applicationErrorMessage } from '../../../core/errors/application-http-error';
import { ConfirmService } from '../../../core/feedback/confirm-service';
import { AuthService } from '../../../core/services/auth-service';
import {
  NOTIFICATION_ICONS,
  NotificationService,
} from '../../../core/services/notification-service';
import { SettingContextService } from '../../../core/settings/setting-context-service';
import { NotificationItem } from '../../../shared/components/notification-item/notification-item';
import { RelativeTimeLabel } from '../../../shared/components/relative-time-label/relative-time-label';
import { PopoverAria } from '../../../shared/directives/popover-aria';
import { NotificationOutputDto } from '../../../shared/dtos/notification.dto';
//#if (!IncludeLocalization)
import { englishText } from '../../../shared/utils/english-text';
//#endif
import { resolveDisplayTimeZone } from '../../../shared/utils/zoned-time';

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
    RelativeTimeLabel,
    ...HlmPopoverImports,
    PopoverAria,
    NotificationItem,
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
    }),
  ],
  templateUrl: './notifications.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Notifications implements OnInit {
  private readonly router = inject(Router);
  private readonly authService = inject(AuthService);
  // 统一按展示时区渲染，避免同一时刻在不同页面显示成不同时间；设置里的时区当前运行时用不了时回落到浏览器时区。
  private readonly displayTimeZoneSetting = inject(SettingContextService).timeZone;
  protected readonly displayTimeZone = computed(() =>
    resolveDisplayTimeZone(this.displayTimeZoneSetting()),
  );
  /** 分钟级时钟："多久以前"随它更新，组件销毁时停止。 */
  protected readonly now = signal(Date.now());
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
  /** 「全部标为已读」请求在途：禁用按钮，避免重复提交。 */
  readonly markingAllRead = signal(false);
  /** 已展开全文的通知；摘要被截断的才有展开按钮。 */
  readonly expanded = signal<ReadonlySet<string>>(new Set());

  constructor() {
    interval(60_000)
      .pipe(takeUntilDestroyed(inject(DestroyRef)))
      .subscribe(() => this.now.set(Date.now()));
  }

  /** 展开 / 收起全文；展开即读完了全文，未读的同时标为已读。 */
  async toggleExpanded(item: NotificationOutputDto): Promise<void> {
    const expanding = !this.expanded().has(item.id);
    this.expanded.update((current) => {
      const next = new Set(current);
      if (!next.delete(item.id)) {
        next.add(item.id);
      }
      return next;
    });
    if (expanding) {
      await this.markAsRead(item);
    }
  }

  /**
   * 等待标已读期间用户已登出，或去了别处（点了别的链接、会话失效转到登录页），就不再跳转、也不动面板，
   * 免得覆盖那次导航。
   */
  async onNotificationClick(item: NotificationOutputDto): Promise<void> {
    const navigation = this.router.lastSuccessfulNavigation();
    if (!(await this.markAsRead(item))) return;
    if (this.router.currentNavigation() || this.router.lastSuccessfulNavigation() !== navigation) {
      return;
    }
    if (item.link) {
      this.notificationOpen.set('closed');
      this.router.navigateByUrl(item.link);
    }
  }

  /**
   * 标记已读是附带动作：失败只提示，不阻断跳转或展开这些主动作。返回发起时的用户是否仍在登录；
   * 等待期间已登出或换了人，失败也不再提示（那是上一个人的请求）。
   */
  private async markAsRead(item: NotificationOutputDto): Promise<boolean> {
    if (item.isRead) {
      return true;
    }
    const userId = this.authService.currentUser()?.id;
    try {
      await this.notificationService.markAsRead(item.id);
    } catch (error: unknown) {
      if (this.authService.currentUser()?.id === userId) this.showRequestError(error);
    }
    return this.authService.currentUser()?.id === userId;
  }

  async markAllNotificationsRead(): Promise<void> {
    this.markingAllRead.set(true);
    try {
      await this.notificationService.markAllAsRead();
    } catch (error: unknown) {
      this.showRequestError(error);
    } finally {
      this.markingAllRead.set(false);
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
  'layout.notifications.clearAll': 'Clear all',
};
//#endif
