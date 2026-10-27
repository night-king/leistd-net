import { DOCUMENT, isPlatformBrowser } from '@angular/common';
import { DestroyRef, Injectable, PLATFORM_ID, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { AccountPreferencesService } from './account-preferences-service';
import { AuthService } from './auth-service';
import { AuthorizationService } from './authorization-service';
import { SettingContextService } from '../settings/setting-context-service';
import { SettingOutputDto } from '../settings/setting.dto';

/**
 * 认证会话上下文：主体确立与离开的唯一入口。当前用户、权限、设置、账户偏好四份状态跟着主体一起建立、
 * 一起清空；偏好由 {@link AccountPreferencesService} 应用到语言与外观，主体离开时由它退回。
 * 主体的建立与离开一律走 {@link establish} 与 {@link clear}，设置刷新走 {@link refreshSettings}，
 * 不单独调下层的 load/clear。
 *
 * 页签回到前台时只重查 `/me`（见 {@link revalidate}）：别处改了偏好或资料、或者会话已经失效，打开着的页签
 * 否则要等下一次操作撞上才知道。权限与设置不随可见事件重查，刷新页面或重新登录时更新：权限只决定界面显示什么，
 * 拦截在服务端；设置很少变，界面用到的生效值已由 `/me` 的偏好给出。
 */
@Injectable({ providedIn: 'root' })
export class SessionContextService {
  private readonly authService = inject(AuthService);
  private readonly authorizationService = inject(AuthorizationService);
  private readonly settingContext = inject(SettingContextService);
  private readonly preferences = inject(AccountPreferencesService);
  /** 在途的重查：页签反复切换时不重复发请求。 */
  private revalidating: Promise<void> | undefined;

  constructor() {
    if (!isPlatformBrowser(inject(PLATFORM_ID))) {
      return;
    }

    const document = inject(DOCUMENT);
    const onVisibilityChange = () => {
      if (document.visibilityState === 'visible') {
        void this.revalidate();
      }
    };
    document.addEventListener('visibilitychange', onVisibilityChange);
    inject(DestroyRef).onDestroy(() =>
      document.removeEventListener('visibilitychange', onVisibilityChange),
    );
  }

  /**
   * 主体确立后建立会话上下文：权限、设置与账户偏好就位。调用方先 `loadUser()`，偏好取自那次 `/me`。
   *
   * 权限失败向上抛：Guard 和菜单按它裁剪，拿不到就只能当无权限，让调用方决定怎么办。
   * 设置失败只清空不抛：它不是进入应用的硬依赖，各消费端有自己的默认行为。
   * 偏好读不到时清掉前一人的语言与时区、退回设备，同样不抛。
   */
  async establish(): Promise<void> {
    await this.authorizationService.initialize();

    try {
      await this.refreshSettings();
    } catch {
      // 不保留上一份快照：主体已经换人，旧快照属于上一个用户。
      this.settingContext.clear();
    }

    // 等账户语言的词条到达再返回：启动流结束外壳才渲染，这样首帧就是账户语言且带着词条——
    // 不等的话外壳先空白一下，渲染后立即发出的一次性提示（如退出模拟）还会取到裸键。
    // 用 /me 发起时取的号：初始化等待期间写入成功或主体离开，那份 /me 已过期，不能盖掉写入结果
    const last = this.authService.lastReadPreferences();
    await this.preferences.applyRead(
      last?.ticket ?? this.preferences.beginRead(),
      last?.read ?? { preferences: null, preferenceOverrides: null },
      'establish',
    );
  }

  /**
   * 重新载入设置，返回本次快照供调用方复用。晚到的旧载入与主体离开前发出的载入由设置上下文自带的代次作废。
   */
  refreshSettings(): Promise<readonly SettingOutputDto[]> {
    return this.settingContext.load();
  }

  /**
   * 重查当前用户（含账户偏好），只在已登录时进行。请求不是静默的：会话已失效（401）时由 HTTP 拦截器清理并转去登录；
   * 其余失败保持现有状态、不提示，这是后台动作，用户没有发起任何操作。
   * 偏好读不到时保留已应用的值：同一主体，旧值比设备值更接近本人的选择。
   */
  revalidate(): Promise<void> {
    if (this.revalidating || !this.authService.isAuthenticated()) {
      return this.revalidating ?? Promise.resolve();
    }

    // 只在发出之后没有更新的写入时才应用：期间保存了资料或主体离开，后台读到的都是旧值
    const userRevision = this.authService.revision;
    // 偏好另有自己的号：期间写入成功或主体离开，迟到的 /me 不盖掉写入结果
    const preferenceTicket = this.preferences.beginRead();
    this.revalidating = firstValueFrom(this.authService.fetchUser(false))
      .then((user) => {
        if (this.authService.revision === userRevision) {
          this.authService.setCurrentUser(user);
        }
        return this.preferences.applyRead(preferenceTicket, user, 'refresh');
      })
      .catch(() => undefined)
      .then(() => {
        this.revalidating = undefined;
      });
    return this.revalidating;
  }

  /** 主体离开：认证数据、权限、设置一起清掉。`logout()` 例外：之后整页跳转，内存状态随页面重建。 */
  clear(): void {
    this.authService.clearAuthData();
    this.authorizationService.clear();
    this.settingContext.clear();
    // 语言已应用到语言服务上，清状态收不回来：不退回的话，共享机器上前一个人的语言会留给下一个人
    void this.preferences.clear();
  }
}
