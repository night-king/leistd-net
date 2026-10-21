import { Injectable, computed, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { SettingService } from './setting-service';
import { SETTINGS } from './setting.constants';
import { SettingOutputDto } from './setting.dto';
//#if (IncludeLocalization)
import { LanguageService } from '../services/language-service';
//#endif

/**
 * 本次会话生效的设置值：启动时加载一次放进 signal，设置页改完调用 {@link load} 刷新；
 * 未登录时为空，读取方回落到自己的默认行为。
 */
@Injectable({ providedIn: 'root' })
export class SettingContextService {
  private readonly settingService = inject(SettingService);
  //#if (IncludeLocalization)
  private readonly languageService = inject(LanguageService);
  //#endif
  private readonly settings = signal<readonly SettingOutputDto[]>([]);
  /** 每次载入与清空都换一代：只有最新一代的响应能写快照，晚到的旧响应与清空前发出的请求一律作废。 */
  private generation = 0;

  /** 当前快照（只读）。设置页据此跟上页外的写入，例如页头的语言切换。 */
  readonly snapshot = this.settings.asReadonly();

  /** 展示时区（IANA 名），未设置时为 `undefined`，按浏览器本地时区展示；时间统一在此读取后换算。 */
  readonly timeZone = computed(() => this.valueOf(SETTINGS.display.timeZone));

  /**
   * 日期书写用的 locale，由界面语言而非时区驱动；取不到时为 `undefined`，由 `appDate` 回落到
   * `YYYY-MM-DD`。
   */
  readonly displayLocale = computed(() => this.resolveLocale());

  /**
   * 载入设置并返回本次快照。失败时向上抛出并保留上一份有效快照，降级由调用点决定：启动阶段可吞掉，
   * 保存后的刷新失败必须让用户看见。
   */
  async load(): Promise<readonly SettingOutputDto[]> {
    const generation = ++this.generation;
    const settings = await firstValueFrom(this.settingService.getSettings());
    // 期间又有更新的载入或已清空（主体离开）：不覆盖，交出当前快照
    if (generation !== this.generation) {
      return this.settings();
    }

    this.settings.set(settings);
    return settings;
  }

  //#if (IncludeLocalization)
  /**
   * 当前界面语言：读运行时的活动语言而非语言设置，后者推导不出未登录时的设备选择，
   * 切换语言后也会与文案分叉。
   */
  //#else
  /** 当前界面语言：单语言项目取浏览器语言，取不到时为 `undefined`。 */
  //#endif
  private resolveLocale(): string | undefined {
    //#if (IncludeLocalization)
    return this.languageService.activeLang();
    //#else
    try {
      return navigator.language || undefined;
    } catch {
      return undefined;
    }
    //#endif
  }

  clear(): void {
    this.generation++;
    this.settings.set([]);
  }

  /** 按回落顺序取生效值：用户覆盖 → 系统默认 → 代码默认。 */
  valueOf(name: string): string | undefined {
    const setting = this.settings().find((s) => s.name === name);
    return setting?.userValue ?? setting?.tenantValue ?? setting?.defaultValue ?? undefined;
  }
}
