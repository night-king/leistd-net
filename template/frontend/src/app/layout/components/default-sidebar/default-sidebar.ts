import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
//#if (IncludeLocalization)
import { TranslocoDirective } from '@jsverse/transloco';
//#endif
import { NgIcon, provideIcons } from '@ng-icons/core';
// prettier-ignore
import {
  lucideBuilding2,
  lucideCheck,
  lucideChevronsUpDown,
  lucideCog,
  lucideDatabase,
  lucideGauge,
  lucideHouse,
  lucideIdCard,
  lucideLayers,
  lucideSearch,
  lucideSettings,
  lucideShieldCheck,
  lucideUsers,
} from '@ng-icons/lucide';
//#if (IncludeRealTime)
import { HlmBadge } from '@spartan-ng/helm/badge';
//#endif
import { HlmDropdownMenuImports } from '@spartan-ng/helm/dropdown-menu';
import { HlmKbdImports } from '@spartan-ng/helm/kbd';
import { HlmSidebarImports, HlmSidebarService } from '@spartan-ng/helm/sidebar';
//#if (IncludeRealTime)
import { HlmTooltipImports } from '@spartan-ng/helm/tooltip';
//#endif

import { CommandPaletteService } from '../../../core/services/command-palette-service';
import { LayoutService } from '../../../core/services/layout-service';
//#if (IncludeRealTime)
import { SignalRService } from '../../../core/services/signalr-service';
//#endif
import { Logo } from '../../../shared/components/logo/logo';
//#if (!IncludeLocalization)
import { englishText } from '../../../shared/utils/english-text';
//#endif
import { MenuItem, NavigationService } from '../../services/navigation-service';
import { UserMenu } from '../user-menu/user-menu';

@Component({
  selector: 'app-default-sidebar',
  standalone: true,
  // prettier-ignore
  imports: [
    RouterLink,
    NgIcon,
    Logo,
    ...HlmDropdownMenuImports,
    ...HlmKbdImports,
    ...HlmSidebarImports,
    //#if (IncludeRealTime)
    HlmBadge,
    ...HlmTooltipImports,
    //#endif
    UserMenu,
    //#if (IncludeLocalization)
    TranslocoDirective,
    //#endif
  ],
  // prettier-ignore
  providers: [provideIcons({
    lucideBuilding2, lucideCheck, lucideChevronsUpDown, lucideCog, lucideHouse,
    lucideGauge, lucideUsers, lucideIdCard, lucideLayers, lucideShieldCheck, lucideSettings,
    lucideDatabase, lucideSearch,
  })],
  // 侧栏用 inset 变体：主内容区（hlmSidebarInset）按兄弟元素的 peer 状态浮成圆角面板，而 hlm-sidebar
  // 包在本组件里、不是它的兄弟，所以把 peer 与桌面端的状态属性转到组件宿主上。手机端侧栏是抽屉，不转。
  host: {
    class: 'peer',
    '[attr.data-variant]': "sidebarService.isMobile() ? null : 'inset'",
    '[attr.data-state]': 'sidebarService.isMobile() ? null : sidebarService.state()',
  },
  templateUrl: './default-sidebar.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DefaultSidebar {
  readonly layoutService = inject(LayoutService);
  //#if (!IncludeLocalization)
  protected readonly t = englishText(ENGLISH);
  //#endif
  protected readonly commandPalette = inject(CommandPaletteService);
  private readonly navigation = inject(NavigationService);
  protected readonly sidebarService = inject(HlmSidebarService);
  //#if (IncludeRealTime)
  protected readonly realtimeStatus = inject(SignalRService).connectionStatus;
  //#endif

  /** 区域切换菜单的弹出方向，同官方 nav-user：桌面向右，手机（侧栏是抽屉）向下。 */
  readonly areaMenuSide = computed(() => (this.sidebarService.isMobile() ? 'bottom' : 'right'));

  readonly areaOptions = this.navigation.areaOptions;
  readonly currentAreaLabel = this.navigation.currentAreaLabel;
  readonly menuGroups = this.navigation.menuGroups;

  goToArea(route: string): void {
    this.navigation.goToArea(route);
  }

  isItemActive(item: MenuItem): boolean {
    return this.navigation.isItemActive(item);
  }
}
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'layout.sidebar.navigation': 'Navigation',
  'layout.sidebar.navigationDescription': 'Browse the sections of this area.',
  'layout.sidebar.switchArea': 'Switch area',
  'layout.sidebar.appTitle': 'Template Project',
  'layout.commandPalette.search': 'Search',
  //#if (IncludeRealTime)
  'layout.realtime.label': 'Real-time',
  'layout.realtime.connected': 'Live updates connected',
  'layout.realtime.reconnecting': 'Connection lost, reconnecting…',
  'layout.realtime.disconnected': 'Live updates offline. Refresh to retry.',
  'layout.realtime.connectedShort': 'Connected',
  'layout.realtime.reconnectingShort': 'Reconnecting…',
  'layout.realtime.disconnectedShort': 'Offline',
  //#endif
};
//#endif
