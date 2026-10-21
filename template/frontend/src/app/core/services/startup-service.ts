// prettier-ignore
import {
  Injectable,
  Injector,
  inject,
  signal,
} from '@angular/core';
import { toObservable } from '@angular/core/rxjs-interop';
import { Router } from '@angular/router';
import { Observable } from 'rxjs';
import { filter, map, take } from 'rxjs/operators';

import { AuthService } from './auth-service';
//#if (Impersonation)
import { ImpersonationService } from './impersonation-service';
//#endif
import { SessionContextService } from './session-context-service';
import { ApplicationHttpError } from '../errors/application-http-error';
import { EntryRouteService } from '../routing/entry-route-service';

export type StartupStatus = 'loading' | 'success' | 'failed';

/**
 * 需要完成认证与会话初始化的顶层路由前缀。
 * 与 app.routes.ts 中使用 authGuard 的路由保持一致，由 app.routes.spec.ts 校验。
 */
export const PROTECTED_ROUTE_PREFIXES = ['/workspace', '/platform'] as const;

@Injectable({ providedIn: 'root' })
export class StartupService {
  private authService = inject(AuthService);
  private readonly sessionContext = inject(SessionContextService);
  private readonly entryRoute = inject(EntryRouteService);
  private readonly router = inject(Router);
  private readonly injector = inject(Injector);
  //#if (Impersonation)
  private readonly impersonation = inject(ImpersonationService);
  //#endif
  private _status = signal<StartupStatus>('loading');
  private _error = signal<unknown | null>(null);

  public readonly status = this._status.asReadonly();
  public readonly error = this._error.asReadonly();

  async load(): Promise<void> {
    this._status.set('loading');
    this._error.set(null);

    //#if (LocalIdentity)
    // 不在这里校验记住的租户：匿名确认"这个租户还在不在"等于给任何人一个枚举接口。
    // 租户失效由服务端在会话恢复中间件里处置（X-Tenant-Invalid 头），前端据此清上下文。
    //#endif
    // 入口路由只认一份读法（见 EntryRouteService.path）：路径按边界比对，不拿整条 URL 去
    // includes——查询串或锚点里的认证路由不代表当前入口，不能影响启动分支。
    //#if (LocalIdentity)
    const route = this.entryRoute.path();

    if (route === '/auth/login') {
      this.sessionContext.clear();
      this._status.set('success');
      return;
    }

    //#endif
    //#if (LocalIdentity)
    if (route === '/auth/callback' || route.startsWith('/auth/external-callback/')) {
      this._status.set('success');
      return;
    }

    //#endif
    if (!this.isProtectedRoute()) {
      this._status.set('success');
      return;
    }

    try {
      await this.authService.initializeAuth();
      // 权限与设置在同一次启动中就位，未就位前按无权限处理，避免受保护入口闪现。
      await this.sessionContext.establish();
      //#if (Impersonation)
      // 模拟态只有服务端的会话声明知道；顶栏的模拟提示要在外壳首帧就位，否则会闪一下"正常会话"。
      await this.impersonation.load();
      //#endif
      this._status.set('success');
    } catch (err: unknown) {
      if (err instanceof ApplicationHttpError && err.status === 401) {
        this.sessionContext.clear();
        this._status.set('success');
      } else {
        this._error.set(err);
        this._status.set('failed');
      }
    }
  }

  /**
   * 重新跑启动流；成功后进入失败时被扣下的导航目标（含查询串与锚点）。
   *
   * 不读地址栏：守卫取消导航后 Router 可能已把它还原成上一条 URL。
   */
  async retry(): Promise<void> {
    await this.load();
    if (this._status() !== 'success') {
      return;
    }
    const target = this.entryRoute.takeHeld();
    if (target) {
      await this.router.navigateByUrl(target);
    }
  }

  /**
   * 路由守卫的共同前置：等启动流离开 `loading`。失败时扣下本次导航目标并给出 `false`，页面停在
   * 启动失败卡片上由重试恢复。启动失败不是"未登录"也不是"无权限"：跳登录页在资源服务形态会与签发方
   * 静默往返成死循环，在本地身份形态会把故障说成"请登录"；跳 403 则是拿空权限下结论。
   *
   * @param targetUrl 守卫拿到的 `RouterStateSnapshot.url`。
   * @returns 启动成功时发出 `true` 后结束，由守卫继续自己的判定。
   */
  settled(targetUrl: string): Observable<boolean> {
    return toObservable(this._status, { injector: this.injector }).pipe(
      filter((status) => status !== 'loading'),
      take(1),
      map((status) => {
        if (status === 'failed') {
          this.entryRoute.hold(targetUrl);
          return false;
        }
        return true;
      }),
    );
  }

  private isProtectedRoute(): boolean {
    const route = this.entryRoute.path();
    return PROTECTED_ROUTE_PREFIXES.some((prefix) => route.startsWith(prefix));
  }
}
