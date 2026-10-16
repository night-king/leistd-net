import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
//#if (IncludeLocalization)
import { toSignal } from '@angular/core/rxjs-interop';
import { TranslocoDirective, TranslocoService } from '@jsverse/transloco';
//#endif
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideRotateCcw } from '@ng-icons/lucide';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmFieldImports } from '@spartan-ng/helm/field';
import { HlmSelectImports } from '@spartan-ng/helm/select';
import { HlmTooltipImports } from '@spartan-ng/helm/tooltip';

import {
  THEME_MODES,
  ThemeMode,
  ThemeService,
} from '../../../../../../core/services/theme-service';
//#if (!IncludeLocalization)
import { englishText } from '../../../../../../shared/utils/english-text';
//#endif

/**
 * 偏好面板里的外观（亮 / 暗 / 跟随系统）：与页头的主题切换共用 {@link ThemeService} 同一份状态，
 * 两处改哪边另一边都跟着变。外观存在本设备，不是账户设置，所以不经设置接口。
 */
@Component({
  selector: 'app-appearance-setting',
  // prettier-ignore
  imports: [
    NgIcon,
    HlmButton,
    ...HlmFieldImports,
    ...HlmSelectImports,
    ...HlmTooltipImports,
    //#if (IncludeLocalization)
    TranslocoDirective,
    //#endif
  ],
  providers: [provideIcons({ lucideRotateCcw })],
  templateUrl: './appearance-setting.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AppearanceSetting {
  protected readonly theme = inject(ThemeService);
  protected readonly modes = THEME_MODES;
  //#if (IncludeLocalization)
  private readonly transloco = inject(TranslocoService);
  private readonly lang = toSignal(this.transloco.langChanges$);

  /** 下拉的取名函数：随语言换一个新函数，选中项的文字才会跟着语言变。 */
  protected readonly labelOf = computed(() => {
    this.lang();
    return (mode: unknown) => this.transloco.translate(`theme.mode.${String(mode)}`);
  });
  //#else
  protected readonly t = englishText(ENGLISH);
  protected readonly labelOf = computed(
    () => (mode: unknown) => this.t(`theme.mode.${String(mode)}`),
  );
  //#endif

  protected select(mode: ThemeMode | null | undefined): void {
    if (mode) {
      this.theme.setMode(mode);
    }
  }
}
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'settings.appearance.label': 'Appearance',
  'settings.appearance.hint': 'Applies to this device only',
  'settings.reset': 'Reset to default',
  'theme.mode.light': 'Light',
  'theme.mode.system': 'System',
  'theme.mode.dark': 'Dark',
};
//#endif
