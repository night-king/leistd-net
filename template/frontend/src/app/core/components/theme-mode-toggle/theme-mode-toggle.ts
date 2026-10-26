import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
//#if (IncludeLocalization)
import { TranslocoDirective, TranslocoService } from '@jsverse/transloco';
//#endif
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideMonitor, lucideMoon, lucideSun } from '@ng-icons/lucide';
import { toast } from '@spartan-ng/brain/sonner';
import { HlmButton } from '@spartan-ng/helm/button';

//#if (!IncludeLocalization)
import { englishText } from '../../../shared/utils/english-text';
//#endif
import { applicationErrorMessage } from '../../errors/application-http-error';
import { AuthService } from '../../services/auth-service';
import { SessionContextService } from '../../services/session-context-service';
import { ThemeService } from '../../services/theme-service';
import { SettingService } from '../../settings/setting-service';
import { SETTINGS } from '../../settings/setting.constants';

/**
 * 主题模式切换按钮：单击在 亮 → 跟随系统 → 暗 三态间循环，图标反映当前模式。
 * 已登录的选择属于账户，写回设置成功后才切换；未登录的选择属于这台设备。
 */
@Component({
  selector: 'app-theme-mode-toggle',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [provideIcons({ lucideSun, lucideMonitor, lucideMoon })],
  // prettier-ignore
  imports: [
    HlmButton, NgIcon,
    //#if (IncludeLocalization)
    TranslocoDirective,
    //#endif
  ],
  templateUrl: './theme-mode-toggle.html',
})
export class ThemeModeToggle {
  readonly themeService = inject(ThemeService);
  private readonly authService = inject(AuthService);
  private readonly settingService = inject(SettingService);
  private readonly sessionContext = inject(SessionContextService);
  //#if (IncludeLocalization)
  private readonly transloco = inject(TranslocoService);
  private readonly text = (key: string): string => this.transloco.translate(key);
  //#else
  protected readonly t = englishText(ENGLISH);
  private readonly text = this.t;
  //#endif

  toggle(): void {
    const mode = this.themeService.nextMode();
    if (!this.authService.isAuthenticated()) {
      this.themeService.transitionTo(mode);
      return;
    }

    // 先写回、成功后再切换：与语言切换器同一做法，偏好页的重取不会早于写入完成
    this.settingService.setForCurrentUser({ name: SETTINGS.display.theme, value: mode }).subscribe({
      next: () => {
        this.themeService.transitionTo(mode);
        // 刷新失败不回退：账户里已经存好了，下次会话建立时快照自然对齐
        this.sessionContext.refreshSettings().catch(() => undefined);
      },
      // 保存失败就不切换，并且说出来，否则用户以为换设备也是这个主题
      error: (error: unknown) =>
        toast.error(this.text('theme.saveFailed'), {
          description: applicationErrorMessage(error),
        }),
    });
  }
}
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'theme.current': 'Theme: {{mode}}',
  'theme.saveFailed': "Appearance wasn't saved; kept as before",
  'theme.mode.light': 'Light',
  'theme.mode.system': 'System',
  'theme.mode.dark': 'Dark',
};
//#endif
