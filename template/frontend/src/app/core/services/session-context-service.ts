import { Injectable, inject } from '@angular/core';

import { AuthService } from './auth-service';
import { AuthorizationService } from './authorization-service';
//#if (IncludeLocalization)
import { SUPPORTED_LANGS, Lang, LanguageService } from './language-service';
//#endif
import { SettingContextService } from '../settings/setting-context-service';
//#if (IncludeLocalization)
import { SETTINGS } from '../settings/setting.constants';
//#endif
import { SettingOutputDto } from '../settings/setting.dto';

/**
 * 认证会话上下文：主体确立与离开的唯一入口。当前用户、权限、设置三份状态跟着主体一起建立、
 * 一起清空；已应用出去的设置（如语言）不在快照里，须显式退回（见 {@link clearSettings}）。
 * 主体的建立与离开一律走 {@link establish} 与 {@link clear}，设置刷新走 {@link refreshSettings}，
 * 不单独调下层的 load/clear。
 */
@Injectable({ providedIn: 'root' })
export class SessionContextService {
  private readonly authService = inject(AuthService);
  private readonly authorizationService = inject(AuthorizationService);
  private readonly settingContext = inject(SettingContextService);
  //#if (IncludeLocalization)
  private readonly languageService = inject(LanguageService);
  //#endif
  /** 每次刷新与清理都换一代：只有最新一次刷新能应用派生状态，晚到的旧刷新与主体离开前发出的刷新一律作废。 */
  private generation = 0;

  /**
   * 主体确立后建立会话上下文：权限与设置就位，并应用由设置派生的界面状态。
   *
   * 权限失败向上抛：Guard 和菜单按它裁剪，拿不到就只能当无权限，让调用方决定怎么办。
   * 设置失败只清空不抛：它不是进入应用的硬依赖，各消费端有自己的默认行为。
   */
  async establish(): Promise<void> {
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
    //#if (IncludeLocalization)
    // 等账户语言的词条到达再返回：启动流结束外壳才渲染，这样首帧就是账户语言且带着词条——
    // 不等的话外壳先空白一下，渲染后立即发出的一次性提示（如退出模拟）还会取到裸键。
    await this.applyLanguageSetting();
    //#endif
    return settings;
  }

  /** 主体离开：认证数据、权限、设置一起清掉。`logout()` 例外：之后整页跳转，内存状态随页面重建。 */
  clear(): void {
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
