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
import { AccountPreferencesService } from '../../services/account-preferences-service';
import { AuthService } from '../../services/auth-service';
import { ThemeService } from '../../services/theme-service';

/**
 * 主题模式切换按钮：单击在 亮 → 跟随系统 → 暗 三态间循环，图标反映当前模式。
 * 已登录的选择属于账户，写入账户偏好成功后才切换；未登录的选择属于这台设备。
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
  private readonly preferences = inject(AccountPreferencesService);
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

    // 写入成功后由偏好状态按响应切换（带过渡）；写入排在偏好队列里，与语言切换、偏好面板的写入不会乱序
    void this.preferences.write('theme', mode).then((outcome) => {
      // 保存失败就不切换，并且说出来，否则用户以为换设备也是这个主题
      if (outcome.status === 'rejected') {
        toast.error(this.text('theme.saveFailed'), {
          description: applicationErrorMessage(outcome.error),
        });
      } else if (outcome.status === 'unconfirmed') {
        // 可能已经保存，不重放；下次重查账户偏好时对齐
        toast.warning(this.text('preferences.saveUnconfirmed'), {
          description: applicationErrorMessage(outcome.error),
        });
      }
    });
  }
}
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'theme.current': 'Theme: {{mode}}',
  'theme.saveFailed': "Appearance wasn't saved; kept as before",
  'preferences.saveUnconfirmed': "Couldn't confirm the change was saved",
  'theme.mode.light': 'Light',
  'theme.mode.system': 'System',
  'theme.mode.dark': 'Dark',
};
//#endif
