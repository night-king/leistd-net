// prettier-ignore
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  //#if (IncludeLocalization)
  effect,
  //#endif
  inject,
} from '@angular/core';
//#if (IncludeLocalization)
import { TranslocoDirective, translateObjectSignal, translateSignal } from '@jsverse/transloco';
//#endif
import { provideIcons } from '@ng-icons/core';
// prettier-ignore
import {
  //#if (LocalIdentity)
  //#if (IncludeNotifications)
  lucideBell,
  //#endif
  lucideShieldCheck,
  lucideUserRound,
  //#endif
  lucideSlidersHorizontal,
} from '@ng-icons/lucide';

import { LayoutService } from '../../../../core/services/layout-service';
//#if (!IncludeLocalization)
import { englishText } from '../../../../shared/utils/english-text';
//#endif
import { SettingsPageState } from '../../settings-page-state';
import { SettingsPanelLink, SettingsShell } from '../../widgets/settings-shell/settings-shell';

/**
 * 个人设置：所有登录用户（含管理人员）同一处，管理平台不另放。面板固定：个人资料与账户安全是专门表单，
 * 「通知」是通知偏好分组，「偏好」收纳其余允许用户覆盖的设置分组（新增用户级设置自动出现）。
 */
@Component({
  selector: 'app-personal-settings',
  // prettier-ignore
  providers: [
    SettingsPageState,
    provideIcons({
      //#if (LocalIdentity)
      //#if (IncludeNotifications)
      lucideBell,
      //#endif
      lucideShieldCheck,
      lucideUserRound,
      //#endif
      lucideSlidersHorizontal,
    }),
  ],
  // prettier-ignore
  imports: [
    SettingsShell,
    //#if (IncludeLocalization)
    TranslocoDirective,
    //#endif
  ],
  templateUrl: './personal-settings.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PersonalSettings {
  private readonly layoutService = inject(LayoutService);
  //#if (IncludeLocalization)
  /** 各面板的标题与说明：整段取成对象，词条到达与语言切换时随之重算；未到达前是空对象。 */
  private readonly panelTexts = translateObjectSignal('settings.panels', {}, { scope: 'settings' });
  private readonly title = translateSignal('settings.personal.title', {}, { scope: 'settings' });

  protected readonly panels = computed<SettingsPanelLink[]>(() => {
    const texts = this.panelTexts();
    const panel = (path: string, icon: string): SettingsPanelLink => ({
      path,
      icon,
      label: texts[path]?.title ?? '',
      description: texts[path]?.description ?? '',
    });
    // 不含本地身份时只剩一项，prettier 会要求折成一行：固定书写形态
    // prettier-ignore
    return [
      //#if (LocalIdentity)
      panel('profile', 'lucideUserRound'),
      panel('security', 'lucideShieldCheck'),
      //#if (IncludeNotifications)
      panel('notifications', 'lucideBell'),
      //#endif
      //#endif
      panel('preferences', 'lucideSlidersHorizontal'),
    ];
  });
  //#else
  protected readonly t = englishText(ENGLISH);

  protected readonly panels = computed<SettingsPanelLink[]>(() => [
    //#if (LocalIdentity)
    {
      path: 'profile',
      icon: 'lucideUserRound',
      label: 'Profile',
      description: 'Your avatar, name and contact details',
    },
    {
      path: 'security',
      icon: 'lucideShieldCheck',
      label: 'Account & security',
      description: 'Your password and how you sign in',
    },
    //#if (IncludeNotifications)
    {
      path: 'notifications',
      icon: 'lucideBell',
      label: 'Notifications',
      description:
        'Choose where each kind of notification reaches you. In-app security alerts are always on; email only goes to a verified address',
    },
    //#endif
    //#endif
    {
      path: 'preferences',
      icon: 'lucideSlidersHorizontal',
      label: 'Preferences',
      description:
        'Language, time zone and appearance. Language and time zone apply to you only; leave blank to follow your organization, or your system when neither the organization nor the deployment sets one',
    },
  ]);
  //#endif

  constructor() {
    // 面包屑末级文案由页面自行设置，与其他页保持同一约定。
    //#if (IncludeLocalization)
    effect(() => this.layoutService.title.set(this.title()));
    //#else
    this.layoutService.title.set(this.t('settings.personal.title'));
    //#endif
  }
}
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'settings.personal.title': 'Personal settings',
  'settings.personal.description': 'Manage your profile, account security and preferences',
  'settings.navigation': 'Settings navigation',
  'settings.openNavigation': 'Open settings navigation',
};
//#endif
