//#if (LocalIdentity)
import { HttpClient } from '@angular/common/http';
import { Injectable, afterNextRender, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

/** 会话的模拟状态，由服务端按会话声明判定。 */
export interface ImpersonationStatus {
  isImpersonating: boolean;
  /** 发起人的显示名（未设置时为用户名）。 */
  impersonatorName?: string;
  /** 所在租户的显示名（未设置时为租户名）。 */
  tenantName?: string;
}

/**
 * 退出模拟后的一次性提示标记：整页跳转会清掉 toast，因此跳转前记在 sessionStorage，
 * 新页面读一次即删，只在本标签页的下一次加载出现一次。
 */
const EXITED_NOTICE_KEY = 'impersonation.exitedNotice';

/**
 * 租户模拟登录。进入与退出都整页跳转：会话被整体替换后权限、菜单与已加载数据全部作废，
 * 与 `AuthService.logout()` 同一处置。
 */
@Injectable({ providedIn: 'root' })
export class ImpersonationService {
  private readonly http = inject(HttpClient);

  private readonly _status = signal<ImpersonationStatus>({ isImpersonating: false });

  /** 当前会话是否处于模拟态；启动流加载一次，之后由整页跳转保持同步。 */
  readonly status = this._status.asReadonly();

  /** 启动时读取一次。失败不阻断启动——模拟提示缺失远好于整个应用起不来。 */
  async load(): Promise<void> {
    try {
      this._status.set(
        await firstValueFrom(this.http.get<ImpersonationStatus>('/api/v1/auth/impersonation')),
      );
    } catch {
      this._status.set({ isImpersonating: false });
    }
  }

  /** 以指定租户管理员身份登录，随后整页进入该租户的工作区。 */
  async start(tenantId: string): Promise<void> {
    await firstValueFrom(this.http.post<void>(`/api/v1/tenants/${tenantId}/impersonate`, {}));
    window.location.href = '/workspace';
  }

  /** 结束模拟，整页回到宿主的管理平台。失败时抛出，由调用方提示并留在原处。 */
  async end(): Promise<void> {
    await firstValueFrom(this.http.post<void>('/api/v1/auth/end-impersonation', {}));
    try {
      sessionStorage.setItem(EXITED_NOTICE_KEY, '1');
    } catch {
      // 存储不可用（隐私模式、被禁用）时只是少一条提示，不能因此拦住跳转。
    }
    window.location.href = '/platform';
  }

  /**
   * 在调用方渲染完成后，若刚刚退出了模拟就执行一次 `notify`。须在组件构造期调用（注入上下文）。
   * 等到渲染之后，是因为 toast 宿主要先挂好；标记读一次即删，刷新不会重复提示。
   */
  notifyAfterRenderIfJustExited(notify: () => void): void {
    afterNextRender(() => {
      if (this.consumeExitedNotice()) {
        notify();
      }
    });
  }

  /** 读取并清除"刚刚退出了模拟"的标记；有则返回 true，只会返回一次。 */
  consumeExitedNotice(): boolean {
    try {
      if (sessionStorage.getItem(EXITED_NOTICE_KEY) === null) {
        return false;
      }
      sessionStorage.removeItem(EXITED_NOTICE_KEY);
      return true;
    } catch {
      return false;
    }
  }
}
//#endif
