import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink, RouterOutlet } from '@angular/router';
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideChevronDown } from '@ng-icons/lucide';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmSeparator } from '@spartan-ng/helm/separator';
import { HlmSheetImports } from '@spartan-ng/helm/sheet';
import { filter, map } from 'rxjs';

/** 设置页左侧的一个面板入口。 */
export interface SettingsPanelLink {
  /** 子路由段，同时是 URL 里的面板名。 */
  path: string;
  label: string;
  icon: string;
  description?: string;
}

/**
 * 设置页外壳：标题、面板导航（宽屏列表、窄屏抽屉）与面板出口，面板清单由宿主给出。面板是子路由，
 * 刷新、分享与后退都按面板走。
 */
@Component({
  selector: 'app-settings-shell',
  imports: [RouterLink, RouterOutlet, NgIcon, HlmButton, HlmSeparator, ...HlmSheetImports],
  providers: [provideIcons({ lucideChevronDown })],
  templateUrl: './settings-shell.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SettingsShell {
  private readonly router = inject(Router);

  readonly heading = input.required<string>();
  readonly description = input('');
  readonly panels = input.required<readonly SettingsPanelLink[]>();
  /** 面板导航的可访问名。 */
  readonly navLabel = input('');
  /** 窄屏导航触发器在没有当前面板时显示的文字。 */
  readonly openNavLabel = input('');

  private readonly url = toSignal(
    this.router.events.pipe(
      filter((event): event is NavigationEnd => event instanceof NavigationEnd),
      map((event) => event.urlAfterRedirects),
    ),
    { initialValue: this.router.url },
  );

  /** 当前面板：URL 最后一段对应的那一项；不在清单里时没有当前面板。 */
  protected readonly activePanel = computed(() => {
    const last = this.url().split(/[?#]/)[0].split('/').pop() ?? '';
    return this.panels().find((panel) => panel.path === last);
  });
}
