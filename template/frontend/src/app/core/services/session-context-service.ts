import { DOCUMENT, isPlatformBrowser } from '@angular/common';
import { DestroyRef, Injectable, PLATFORM_ID, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { AuthService } from './auth-service';
import { AuthorizationService } from './authorization-service';
//#if (IncludeLocalization)
import { SUPPORTED_LANGS, Lang, LanguageService } from './language-service';
//#endif
import { ThemeService } from './theme-service';
import { SettingContextService } from '../settings/setting-context-service';
import { SETTINGS } from '../settings/setting.constants';
import { SettingOutputDto } from '../settings/setting.dto';

/**
 * 认证会话上下文：主体确立与离开的唯一入口。当前用户、权限、设置三份状态跟着主体一起建立、
 * 一起清空；已应用出去的设置（如语言）不在快照里，须显式退回（见 {@link clearSettings}）。外观例外：
 * 它不含个人信息，主体离开后保留最近一次值，作为登录页与下次首帧的主题（见 {@link ThemeService}）。
 * 主体的建立与离开一律走 {@link establish} 与 {@link clear}，设置刷新走 {@link refreshSettings}，
 * 不单独调下层的 load/clear。
 *
 * 页签回到前台时重查一遍（见 {@link revalidate}）：别处改了权限或偏好、或者已经退出，打开着的页签
 * 否则要等下一次操作撞上才知道。
 */
@Injectable({ providedIn: 'root' })
export class SessionContextService {
  private readonly authService = inject(AuthService);
  private readonly authorizationService = inject(AuthorizationService);
  private readonly settingContext = inject(SettingContextService);
  //#if (IncludeLocalization)
  private readonly languageService = inject(LanguageService);
  //#endif
  private readonly themeService = inject(ThemeService);
  /** 每次刷新与清理都换一代：只有最新一次刷新能应用派生状态，晚到的旧刷新与主体离开前发出的刷新一律作废。 */
  private generation = 0;
  /** 主体每确立或离开一次加一：重查返回时主体已经换过，结果属于上一个人，不应用。 */
  private subject = 0;
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
   * 主体确立后建立会话上下文：权限与设置就位，并应用由设置派生的界面状态。
   *
   * 权限失败向上抛：Guard 和菜单按它裁剪，拿不到就只能当无权限，让调用方决定怎么办。
   * 设置失败只清空不抛：它不是进入应用的硬依赖，各消费端有自己的默认行为。
   */
  async establish(): Promise<void> {
    this.subject++;
    await this.authorizationService.initialize();

    try {
      await this.refreshSettings();
    } catch {
      // 不保留上一份快照：主体已经换人，旧快照属于上一个用户。
      await this.clearSettings();
    }
  }

  /**
   * 重新载入设置并应用由它派生的界面状态，返回本次快照供调用方复用。设置页保存后必须走这里
   * 而非 `SettingContextService.load()`：只刷新数据不重新应用，界面会停在旧值。
   */
  async refreshSettings(): Promise<readonly SettingOutputDto[]> {
    const generation = ++this.generation;
    const settings = await this.settingContext.load();
    if (generation !== this.generation) {
      return settings;
    }

    this.themeService.applyAccountMode(this.settingContext.valueOf(SETTINGS.display.theme));
    //#if (IncludeLocalization)
    // 等账户语言的词条到达再返回：启动流结束外壳才渲染，这样首帧就是账户语言且带着词条——
    // 不等的话外壳先空白一下，渲染后立即发出的一次性提示（如退出模拟）还会取到裸键。
    await this.applyLanguageSetting();
    //#endif
    return settings;
  }

  /**
   * 重查当前用户、权限与设置，只在已登录时进行。设置请求不是静默的：会话已失效（401）时由 HTTP 拦截器
   * 清理并转去登录；其余失败保持现有状态、不提示，这是后台动作，用户没有发起任何操作。
   */
  revalidate(): Promise<void> {
    if (this.revalidating || !this.authService.isAuthenticated()) {
      return this.revalidating ?? Promise.resolve();
    }

    const subject = this.subject;
    const unchanged = () => subject === this.subject;
    this.revalidating = Promise.allSettled([
      firstValueFrom(this.authService.fetchUser()).then((user) => {
        if (unchanged()) {
          this.authService.setCurrentUser(user);
        }
      }),
      firstValueFrom(this.authorizationService.fetch()).then((permissions) => {
        if (unchanged()) {
          this.authorizationService.setPermissions(permissions);
        }
      }),
      // 自带代次：主体离开后才返回的快照不会应用
      this.refreshSettings(),
    ]).then(() => {
      this.revalidating = undefined;
    });
    return this.revalidating;
  }

  /** 主体离开：认证数据、权限、设置一起清掉。`logout()` 例外：之后整页跳转，内存状态随页面重建。 */
  clear(): void {
    this.subject++;
    this.authService.clearAuthData();
    this.authorizationService.clear();
    void this.clearSettings();
  }

  /** 清掉跟着主体走的设置状态；新增跟主体走的派生状态时只改这里。 */
  private clearSettings(): Promise<void> {
    this.generation++;
    this.settingContext.clear();
    //#if (IncludeLocalization)
    // 语言不在快照里：它已经应用到 LanguageService 上了，清快照收不回来，
    // 于是共享机器上前一个人的语言会留给下一个人。
    return this.languageService.resetToDeviceLang();
    //#else
    return Promise.resolve();
    //#endif
  }
  //#if (IncludeLocalization)

  /** 把账户设置里的界面语言应用到本次会话：已登录以账户设置为准，切换器负责把选择写回设置。 */
  private applyLanguageSetting(): Promise<void> {
    const value = this.settingContext.valueOf(SETTINGS.display.language);
    if (value && (SUPPORTED_LANGS as readonly string[]).includes(value)) {
      return this.languageService.applyAccountLang(value as Lang);
    }

    // 取不到或不支持的语言退回设备偏好；什么都不做会沿用上一个主体的语言。
    return this.languageService.resetToDeviceLang();
  }
  //#endif
}
