import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { lastValueFrom } from 'rxjs';

import { SignalRService } from './signalr-service';
import { NotificationOutputDto } from '../../shared/dtos/notification.dto';

/**
 * 各通知类型的图标。类型由后端业务定义（见 `AppNotificationTypes`），新增类型时在这里补一行；
 * 没登记的（包括默认的系统通知）用通用图标。
 */
// prettier-ignore
const TYPE_ICONS: Readonly<Record<string, string>> = {
  //#if (LocalIdentity)
  Security: 'lucideShieldAlert',
  //#endif
};

/** 通知管理：通知列表与已读（HTTP）+ SignalR 实时推送桥接。 */
@Injectable({ providedIn: 'root' })
export class NotificationService {
  private readonly http = inject(HttpClient);
  private readonly signalR = inject(SignalRService);

  /** 通知列表（与 SignalR 推送共享同一信号）。 */
  readonly notifications = this.signalR.notifications;

  readonly unreadCount = this.signalR.unreadCount;

  readonly loading = signal(false);

  /**
   * 最近一次加载历史失败。界面据此区分"没有通知"与"没加载出来"，并提供重试；
   * 已经推送到的通知不受影响，仍然显示。
   */
  readonly loadFailed = signal(false);

  /** 初始化：加载历史通知 + 连接 SignalR。 */
  async init(): Promise<void> {
    const generation = this.signalR.authGeneration;

    await this.loadNotifications();

    // 加载期间主体已切换：这一轮属于上一个用户，不再建连，否则连接会以上一个人的身份被复用。
    if (!this.signalR.isCurrentGeneration(generation)) {
      return;
    }

    await this.signalR.connect();
  }

  async loadNotifications(maxCount = 50): Promise<void> {
    // 记下发起请求时的认证代际：请求在途时登出/换人登录，响应回来仍会写同一个
    // 共享 signal——那就是把上一个用户的历史通知落到下一个人的界面上。
    const generation = this.signalR.authGeneration;

    this.loading.set(true);
    this.loadFailed.set(false);
    try {
      const items = await lastValueFrom(
        this.http.get<NotificationOutputDto[]>('/api/v1/notifications', {
          params: { maxCount: maxCount.toString() },
        }),
      );

      const incoming = items ?? [];
      // 合并 SignalR 已推送但不在历史中的通知
      const pushed = this.signalR.notifications();
      const merged = [...incoming];
      for (const n of pushed) {
        if (!merged.some((m) => m.id === n.id)) {
          merged.unshift(n);
        }
      }
      merged.sort(
        (a, b) => new Date(b.creationTime).getTime() - new Date(a.creationTime).getTime(),
      );

      if (!this.signalR.isCurrentGeneration(generation)) {
        return;
      }

      this.signalR.notifications.set(merged);
    } catch (err) {
      console.error('[NotificationService] Load failed:', err);
      // 主体已切换：这次失败属于上一个用户，不能标到下一个人的界面上。
      if (this.signalR.isCurrentGeneration(generation)) {
        this.loadFailed.set(true);
      }
    } finally {
      // 同理：旧主体的请求结束时，新主体的加载可能正在进行，不能替它关掉加载状态。
      if (this.signalR.isCurrentGeneration(generation)) {
        this.loading.set(false);
      }
    }
  }

  async markAsRead(notificationId: string): Promise<void> {
    const generation = this.signalR.authGeneration;
    await lastValueFrom(this.http.put(`/api/v1/notifications/${notificationId}/read`, {}));

    if (!this.signalR.isCurrentGeneration(generation)) {
      return;
    }

    this.signalR.notifications.update((list) =>
      list.map((n) => (n.id === notificationId ? { ...n, isRead: true } : n)),
    );
  }

  /**
   * 全部标记已读。写操作失败时抛出（拦截器已归一化为 ApplicationHttpError），
   * 由调用方决定反馈方式；本地状态只在成功后更新。
   */
  async markAllAsRead(): Promise<void> {
    const generation = this.signalR.authGeneration;
    await lastValueFrom(this.http.put('/api/v1/notifications/read-all', {}));

    if (!this.signalR.isCurrentGeneration(generation)) {
      return;
    }

    this.signalR.notifications.update((list) => list.map((n) => ({ ...n, isRead: true })));
  }

  /** 清空全部通知（持久删除）。 */
  async clearAll(): Promise<void> {
    const generation = this.signalR.authGeneration;
    await lastValueFrom(this.http.delete('/api/v1/notifications'));

    // A 的 clearAll 在途、B 登录并加载完自己的列表，这一句会把 B 的列表清空。
    if (!this.signalR.isCurrentGeneration(generation)) {
      return;
    }

    this.signalR.notifications.set([]);
  }

  /** 删除单条通知（持久删除）。 */
  async clearOne(notificationId: string): Promise<void> {
    const generation = this.signalR.authGeneration;
    await lastValueFrom(this.http.delete(`/api/v1/notifications/${notificationId}`));

    if (!this.signalR.isCurrentGeneration(generation)) {
      return;
    }

    this.signalR.notifications.update((list) => list.filter((n) => n.id !== notificationId));
  }

  /** 通知类型图标（lucide 图标名；仅区分形状，颜色统一由视图控制）。 */
  getIcon(type: string): string {
    return TYPE_ICONS[type] ?? 'lucideInfo';
  }
}
