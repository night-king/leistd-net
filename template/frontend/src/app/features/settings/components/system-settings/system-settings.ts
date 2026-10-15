import { ChangeDetectionStrategy, Component, computed, effect, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, NavigationEnd, Router } from '@angular/router';
//#if (IncludeLocalization)
import { TranslocoDirective, translateSignal } from '@jsverse/transloco';
//#endif
import { provideIcons } from '@ng-icons/core';
import {
  lucideActivity,
  lucideArchive,
  //#if (ExternalLogin)
  lucideLogIn,
  //#endif
  lucideMail,
  lucideSettings,
  lucideShieldCheck,
  lucideUserPlus,
} from '@ng-icons/lucide';
import { HlmButton } from '@spartan-ng/helm/button';
import { filter, map } from 'rxjs';

import { LayoutService } from '../../../../core/services/layout-service';
//#if (!IncludeLocalization)
import { englishText } from '../../../../shared/utils/english-text';
//#endif
// prettier-ignore
import {
  groupPath,
  groupSettings,
  settingsInScope,
  SettingsPageState,
} from '../../settings-page-state';
import { SettingsPanelLink, SettingsShell } from '../../widgets/settings-shell/settings-shell';

/**
 * 各分组在面板导航上的图标。只影响外观：没登记的分组照样出现，用通用图标。
 * 面板本身由后端分组决定，这里**不是**面板清单。
 */
const GROUP_ICONS: Readonly<Record<string, string>> = {
  Registration: 'lucideUserPlus',
  Security: 'lucideShieldCheck',
  Email: 'lucideMail',
  //#if (ExternalLogin)
  ExternalLogin: 'lucideLogIn',
  //#endif
  Operations: 'lucideActivity',
  Audit: 'lucideArchive',
};

/**
 * 系统设置：本租户（或宿主）下所有人的默认值与策略。一个后端分组就是一个面板，名称、顺序与可见性
 * 都来自后端，前端不另列面板清单。
 */
@Component({
  selector: 'app-system-settings',
  providers: [
    SettingsPageState,
    provideIcons({
      lucideActivity,
      lucideArchive,
      //#if (ExternalLogin)
      lucideLogIn,
      //#endif
      lucideMail,
      lucideSettings,
      lucideShieldCheck,
      lucideUserPlus,
    }),
  ],
  // prettier-ignore
  imports: [
    HlmButton,
    SettingsShell,
    //#if (IncludeLocalization)
    TranslocoDirective,
    //#endif
  ],
  templateUrl: './system-settings.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SystemSettings {
  private readonly layoutService = inject(LayoutService);
  private readonly pageState = inject(SettingsPageState);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  //#if (!IncludeLocalization)
  protected readonly t = englishText(ENGLISH);
  //#endif

  protected readonly panels = computed<SettingsPanelLink[]>(() =>
    groupSettings(settingsInScope(this.pageState.settings(), 'system')).map((group) => ({
      path: groupPath(group.key),
      label: group.label,
      icon: GROUP_ICONS[group.key] ?? 'lucideSettings',
    })),
  );

  private readonly url = toSignal(
    this.router.events.pipe(
      filter((event): event is NavigationEnd => event instanceof NavigationEnd),
      map((event) => event.urlAfterRedirects),
    ),
    { initialValue: this.router.url },
  );

  protected readonly loading = this.pageState.loading;

  /**
   * 首次加载失败的原因，仅在还没进入任何面板时由本页显示：
   * 带着面板地址进来时，面板自己会显示同一个错误态。
   */
  protected readonly loadError = computed(() => {
    this.url(); // 子路由随导航变化，地址变了要重算
    return this.route.firstChild?.snapshot.paramMap.has('group')
      ? null
      : this.pageState.loadError();
  });

  /** 错误态里的重试：取回后面板导航出现，再按惯例落到第一个面板。 */
  protected reload(): void {
    this.pageState.load();
  }

  constructor() {
    //#if (IncludeLocalization)
    const title = translateSignal('settings.system.title', {}, { scope: 'settings' });
    effect(() => this.layoutService.title.set(title()));
    //#else
    this.layoutService.title.set(this.t('settings.system.title'));
    //#endif

    // 没带面板（直接进 /platform/settings）或带了一个不存在的面板（旧链接、换了上下文）时，
    // 落到第一个面板。面板要等设置取回才知道，所以不能写成路由表里的静态重定向。
    effect(() => {
      const panels = this.panels();
      if (!this.pageState.loaded() || panels.length === 0) {
        return;
      }

      const current = this.url().split(/[?#]/)[0].split('/').pop();
      if (!panels.some((panel) => panel.path === current)) {
        void this.router.navigate([panels[0].path], { relativeTo: this.route, replaceUrl: true });
      }
    });
  }
}
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'settings.system.title': 'System settings',
  'settings.system.description':
    'Defaults and policies for everyone here; each user may override their own preferences',
  'settings.navigation': 'Settings navigation',
  'settings.openNavigation': 'Open settings navigation',
  'settings.loadFailed': "Couldn't load settings",
  'common.retry': 'Retry',
};
//#endif
