import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { SettingService } from './setting-service';
import { SettingOutputDto } from './setting.dto';

/**
 * 本次会话生效的设置值：启动时加载一次放进 signal，设置页改完调用 {@link load} 刷新；
 * 未登录时为空，读取方回落到自己的默认行为。账户偏好（语言、时区、外观）不在这里，
 * 见 `AccountPreferencesService`。
 */
@Injectable({ providedIn: 'root' })
export class SettingContextService {
  private readonly settingService = inject(SettingService);
  private readonly settings = signal<readonly SettingOutputDto[]>([]);
  /** 每次载入与清空都换一代：只有最新一代的响应能写快照，晚到的旧响应与清空前发出的请求一律作废。 */
  private generation = 0;

  /** 当前快照（只读）。 */
  readonly snapshot = this.settings.asReadonly();

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
