import { Location } from '@angular/common';
import { Injectable, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NavigationCancel, Router } from '@angular/router';
import { filter } from 'rxjs/operators';

/**
 * 当前停在哪条应用路由上，读自 Angular 的 `Location`。不读 `Router.url`：启动流跑在初始导航之前，
 * 那时它一律是 `/`；`Location` 按路径或哈希策略直接解析地址栏，初始导航前后都准。
 *
 * 启动失败时守卫取消导航并扣下目标（{@link hold}）：此后入口以目标为准，直到重试成功后取走。
 */
@Injectable({ providedIn: 'root' })
export class EntryRouteService {
  private readonly location = inject(Location);
  private heldTarget: string | null = null;

  constructor() {
    // 守卫取消导航后 Router 按 canceledNavigationResolution 把地址栏还原成上一条（首次导航是 /）；
    // 扣着目标时放回去，用户手动刷新也回到原目标。
    inject(Router)
      .events.pipe(
        filter((event) => event instanceof NavigationCancel),
        takeUntilDestroyed(),
      )
      .subscribe(() => {
        if (this.heldTarget) {
          this.location.replaceState(this.heldTarget);
        }
      });
  }

  /**
   * 应用内 URL，保留查询串（用作落地地址）。扣着导航目标时返回该目标。
   *
   * `Location.path()` 对站点根给出 `''`、对带查询串的根给出 `?a=1`，这里统一补成以 `/` 开头。
   */
  url(): string {
    if (this.heldTarget) {
      return this.heldTarget;
    }
    const path = this.location.path();
    return path.startsWith('/') ? path : `/${path}`;
  }

  /**
   * 应用路径（去掉查询串与锚点），供路由归类：查询串里可能出现 `/auth/callback`，不能对整条 URL
   * 做 includes。
   */
  path(): string {
    return this.url().split(/[?#]/)[0];
  }

  /**
   * 当前是否停在认证路由上（登录页、OIDC 回调、外部登录回调）。
   *
   * 这些路由上正有一条认证流程在跑：它自己知道该怎么处置 401，别处不要插手。
   */
  isOnAuthRoute(): boolean {
    return this.path().startsWith('/auth/');
  }

  /**
   * 扣下被启动失败拦住的导航目标（含查询串与锚点）。地址栏可能已被还原，重试不能读它。
   */
  hold(url: string): void {
    this.heldTarget = url;
  }

  /** 取走扣下的目标并清除；没有时返回 `null`。 */
  takeHeld(): string | null {
    const target = this.heldTarget;
    this.heldTarget = null;
    return target;
  }
}
