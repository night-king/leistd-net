import { DestroyRef, inject, Injectable, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
//#if (IncludeLocalization)
import { TranslocoService } from '@jsverse/transloco';
//#endif
import { toast } from '@spartan-ng/brain/sonner';
import { catchError, EMPTY, finalize, Subscription } from 'rxjs';

import { applicationErrorMessage } from '../../core/errors/application-http-error';
import { SettingService } from '../../core/settings/setting-service';
import { SettingOutputDto } from '../../core/settings/setting.dto';

/** 设置作用域：`account` 写当前用户偏好；`system` 写当前租户（或宿主）下所有人的默认值，写入授权不同。 */
export type SettingScope = 'account' | 'system';

/** 一个分组及归到它下面的设置项；分组标识、顺序与显示名都来自后端。 */
export interface SettingGroup {
  key: string;
  label: string;
  settings: SettingOutputDto[];
}

/** 本作用域下能看到的设置。 */
export function settingsInScope(
  settings: readonly SettingOutputDto[],
  scope: SettingScope,
): SettingOutputDto[] {
  // 系统页：允许租户级或进程级（后者两个层级标记都是 false，只看 allowsTenantScope 会漏掉），
  // 且当前用户有该分组的管理权限；正确性以服务端算出的 canManage 为准
  return scope === 'account'
    ? settings.filter((s) => s.allowsUserScope)
    : settings.filter((s) => (s.allowsTenantScope || s.allowsHostScope) && s.canManage);
}

/** 按分组切分，分组标识与顺序来自后端（未分组的已归入 `Other`），前端不另列清单。 */
export function groupSettings(settings: readonly SettingOutputDto[]): SettingGroup[] {
  const byKey = new Map<string, SettingGroup>();
  for (const setting of settings) {
    const group = byKey.get(setting.group) ?? {
      key: setting.group,
      label: setting.groupDisplayName || setting.group,
      settings: [],
    };
    group.settings.push(setting);
    byKey.set(setting.group, group);
  }

  return [...byKey.values()];
}

/** 分组标识在 URL 里的写法：`RegistrationSecurity` → `registration-security`。 */
export function groupPath(key: string): string {
  return key.replace(/([a-z0-9])([A-Z])/g, '$1-$2').toLowerCase();
}

/**
 * 一个设置页（个人或系统设置）共用的设置快照：外壳据此决定面板，面板据此渲染与写入，
 * 避免重复请求以及导航与内容不一致。
 */
@Injectable()
export class SettingsPageState {
  private readonly settingService = inject(SettingService);
  private readonly destroyRef = inject(DestroyRef);
  //#if (IncludeLocalization)
  private readonly transloco = inject(TranslocoService);
  //#endif

  readonly settings = signal<SettingOutputDto[]>([]);
  readonly loading = signal(false);
  /** 至少成功取到过一次。外壳据此决定何时把空地址导向第一个面板。 */
  readonly loaded = signal(false);
  /** 还没有快照可保留时的加载失败原因：有值时页面显示错误态与重试，不显示"暂无可配置项"。 */
  readonly loadError = signal<string | null>(null);

  constructor() {
    this.load();
    //#if (IncludeLocalization)

    // 显示名由后端按请求语言本地化，切换语言须重取。langChanges$ 订阅时先发出当前语言，
    // 比对上次的语言以免进页面白发一次请求。
    let seenLang = this.transloco.getActiveLang();
    this.transloco.langChanges$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((lang) => {
      if (lang === seenLang) {
        return;
      }

      seenLang = lang;
      this.load();
    });
    //#endif
  }

  /** 尚未返回的上一次读取。新一次开始时取消它：晚到的旧响应不能覆盖新快照。 */
  private pending?: Subscription;

  load(): void {
    this.pending?.unsubscribe();
    this.loading.set(true);
    this.pending = this.settingService
      .getSettings()
      .pipe(
        catchError((error: unknown) => {
          // 已有快照时刷新失败（切换语言、写入后重取）：保留原内容，只做提示
          if (this.loaded()) {
            toast.error(applicationErrorMessage(error));
          } else {
            this.loadError.set(applicationErrorMessage(error));
          }
          return EMPTY;
        }),
        finalize(() => this.loading.set(false)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((settings) => {
        this.loadError.set(null);
        this.settings.set(settings);
        this.loaded.set(true);
      });
  }
}
