import { ChangeDetectionStrategy, Component, DestroyRef, inject, input } from '@angular/core';
//#if (IncludeLocalization)
//#if (Impersonation)
import { TranslocoDirective, TranslocoService } from '@jsverse/transloco';
//#else
import { TranslocoDirective } from '@jsverse/transloco';
//#endif
//#endif
import { NgIcon, provideIcons } from '@ng-icons/core';
//#if (Impersonation)
import { lucideSearch, lucideVenetianMask } from '@ng-icons/lucide';
import { toast } from '@spartan-ng/brain/sonner';
//#else
import { lucideSearch } from '@ng-icons/lucide';
//#endif
import { HlmBreadcrumbImports } from '@spartan-ng/helm/breadcrumb';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmKbdImports } from '@spartan-ng/helm/kbd';
//#if (Impersonation)
import { HlmPopoverImports } from '@spartan-ng/helm/popover';
//#endif
import { HlmSeparatorImports } from '@spartan-ng/helm/separator';
import { HlmSidebarTrigger } from '@spartan-ng/helm/sidebar';
import { HlmTooltipImports } from '@spartan-ng/helm/tooltip';

//#if (IncludeLocalization)
import { LanguageSwitcher } from '../../../core/components/language-switcher/language-switcher';
//#endif
import { ThemeModeToggle } from '../../../core/components/theme-mode-toggle/theme-mode-toggle';
//#if (Impersonation)
import { applicationErrorMessage } from '../../../core/errors/application-http-error';
import { ImpersonationService } from '../../../core/services/impersonation-service';
//#endif
import { LayoutService } from '../../../core/services/layout-service';
//#if (Impersonation)
import { PopoverAria } from '../../../shared/directives/popover-aria';
//#endif
//#if (!IncludeLocalization)
import { englishText } from '../../../shared/utils/english-text';
//#endif
import { CommandPaletteService } from '../../services/command-palette-service';
//#if (IncludeNotifications)
import { Notifications } from '../notifications/notifications';
//#endif
import { UserMenu } from '../user-menu/user-menu';
import { WorkspaceNav } from '../workspace-nav/workspace-nav';

@Component({
  selector: 'app-default-header',
  standalone: true,
  imports: [
    // 基础布局依赖；通知 / 本地化组件按条件补充。
    HlmButton,
    NgIcon,
    ThemeModeToggle,
    HlmSidebarTrigger,
    ...HlmBreadcrumbImports,
    ...HlmKbdImports,
    ...HlmSeparatorImports,
    ...HlmTooltipImports,
    UserMenu,
    WorkspaceNav,
    //#if (Impersonation)
    ...HlmPopoverImports,
    PopoverAria,
    //#endif
    //#if (IncludeNotifications)
    Notifications,
    //#endif
    //#if (IncludeLocalization)
    LanguageSwitcher,
    TranslocoDirective,
    //#endif
  ],
  //#if (Impersonation)
  providers: [provideIcons({ lucideSearch, lucideVenetianMask })],
  //#else
  providers: [provideIcons({ lucideSearch })],
  //#endif
  templateUrl: './default-header.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DefaultHeader {
  /**
   * `sidebar`：配合侧栏，左侧是折叠按钮与面包屑，头像在侧栏底部。
   * `topbar`：没有侧栏，左侧是品牌与导航，头像放在最右。
   */
  readonly layout = input<'sidebar' | 'topbar'>('sidebar');

  readonly layoutService = inject(LayoutService);
  protected readonly commandPalette = inject(CommandPaletteService);
  //#if (!IncludeLocalization)
  protected readonly t = englishText(ENGLISH);
  //#endif
  //#if (Impersonation)
  readonly impersonation = inject(ImpersonationService);
  //#if (IncludeLocalization)
  private readonly transloco = inject(TranslocoService);
  //#endif
  //#endif

  constructor() {
    // 离开布局时清掉页面标题：页头随布局销毁、早于下一页设置标题，不清的话认证页会挂着上一页的标题。
    inject(DestroyRef).onDestroy(() => this.layoutService.title.set(''));
    //#if (Impersonation)

    // 退出模拟是整页跳转，提示只能在新页面上补（见 ImpersonationService 的一次性标记）。
    //#if (IncludeLocalization)
    this.impersonation.notifyAfterRenderIfJustExited(() =>
      toast.success(this.transloco.translate('impersonation.exited')),
    );
    //#else
    this.impersonation.notifyAfterRenderIfJustExited(() =>
      toast.success('Returned to your own account.'),
    );
    //#endif
    //#endif
  }
  //#if (Impersonation)

  /** 结束模拟。失败时不跳转，保留顶栏的模拟状态并说明原因。 */
  async endImpersonation(): Promise<void> {
    try {
      await this.impersonation.end();
    } catch (error) {
      //#if (IncludeLocalization)
      toast.error(this.transloco.translate('impersonation.exitFailed'), {
        description: applicationErrorMessage(error),
      });
      //#else
      toast.error('Could not end impersonation.', { description: applicationErrorMessage(error) });
      //#endif
    }
  }
  //#endif
}
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'common.toggleSidebar': 'Toggle sidebar',
  'layout.commandPalette.search': 'Search',
  'menu.platform': 'Admin platform',
  'menu.workspace': 'Workspace',
  'impersonation.banner': 'Acting as tenant {{tenant}} · started by {{impersonator}}',
  'impersonation.badge': 'Impersonating · {{tenant}}',
  'impersonation.title': 'Impersonating a tenant',
  'impersonation.note': "Everything you do now is recorded under this tenant's account.",
  'impersonation.exit': 'Exit impersonation',
};
//#endif
