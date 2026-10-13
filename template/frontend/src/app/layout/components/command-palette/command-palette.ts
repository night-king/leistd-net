import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { Router } from '@angular/router';
//#if (IncludeLocalization)
import { TranslocoDirective } from '@jsverse/transloco';
//#endif
import { NgIcon, provideIcons } from '@ng-icons/core';
// prettier-ignore
import {
  lucideBuilding2,
  lucideCog,
  lucideDatabase,
  lucideGauge,
  lucideHouse,
  lucideIdCard,
  lucideLayers,
  lucideSettings,
  lucideShieldCheck,
  lucideUsers,
} from '@ng-icons/lucide';
import { BrnDialogState } from '@spartan-ng/brain/dialog';
import { HlmCommandImports } from '@spartan-ng/helm/command';

import { CommandPaletteService } from '../../../core/services/command-palette-service';
//#if (!IncludeLocalization)
import { englishText } from '../../../shared/utils/english-text';
//#endif
import { NavigationService } from '../../services/navigation-service';

/**
 * 命令面板：列出当前区按权限裁剪后的菜单与可切换的区域，选中即跳转。每个布局放一个，⌘K / Ctrl+K
 * 开合；入口按钮只调 {@link CommandPaletteService}。
 */
@Component({
  selector: 'app-command-palette',
  standalone: true,
  // prettier-ignore
  imports: [
    NgIcon,
    ...HlmCommandImports,
    //#if (IncludeLocalization)
    TranslocoDirective,
    //#endif
  ],
  // 图标与侧栏菜单一致：菜单定义里的图标名在这里也要登记
  // prettier-ignore
  providers: [provideIcons({
    lucideBuilding2, lucideCog, lucideDatabase, lucideGauge, lucideHouse, lucideIdCard,
    lucideLayers, lucideSettings, lucideShieldCheck, lucideUsers,
  })],
  host: { '(document:keydown)': 'onKeydown($event)' },
  templateUrl: './command-palette.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CommandPalette {
  //#if (!IncludeLocalization)
  protected readonly t = englishText(ENGLISH);
  //#endif
  protected readonly palette = inject(CommandPaletteService);
  private readonly navigation = inject(NavigationService);
  private readonly router = inject(Router);

  protected readonly menuGroups = this.navigation.menuGroups;
  protected readonly otherAreas = computed(() =>
    this.navigation.areaOptions().filter((area) => !area.current),
  );

  protected onKeydown(event: KeyboardEvent): void {
    if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === 'k') {
      event.preventDefault();
      this.palette.toggle();
    }
  }

  protected onStateChange(state: BrnDialogState): void {
    if (state === 'closed') {
      this.palette.close();
    }
  }

  protected go(route: string): void {
    this.palette.close();
    void this.router.navigateByUrl(route);
  }
}
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'layout.commandPalette.title': 'Command palette',
  'layout.commandPalette.description': 'Search for a page to open.',
  'layout.commandPalette.placeholder': 'Search pages…',
  'layout.commandPalette.empty': 'No matching pages.',
  'layout.sidebar.switchArea': 'Switch area',
};
//#endif
