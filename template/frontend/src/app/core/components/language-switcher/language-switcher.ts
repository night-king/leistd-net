import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { TranslocoDirective, TranslocoService } from '@jsverse/transloco';
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideCheck, lucideGlobe } from '@ng-icons/lucide';
import { toast } from '@spartan-ng/brain/sonner';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmDropdownMenuImports } from '@spartan-ng/helm/dropdown-menu';

import { applicationErrorMessage } from '../../errors/application-http-error';
import { AuthService } from '../../services/auth-service';
import { Lang, LanguageService } from '../../services/language-service';
import { SessionContextService } from '../../services/session-context-service';
import { SettingService } from '../../settings/setting-service';
import { SETTINGS } from '../../settings/setting.constants';

interface LangOption {
  id: Lang;
  label: string;
  active: boolean;
}

/** 语言切换器：图标按钮弹出下拉菜单，当前语言打勾；用于各布局右上角操作区。只支持一种语言时不显示。 */
@Component({
  selector: 'app-language-switcher',
  standalone: true,
  imports: [HlmButton, NgIcon, TranslocoDirective, ...HlmDropdownMenuImports],
  providers: [provideIcons({ lucideGlobe, lucideCheck })],
  host: { class: 'inline-flex items-center', '[class.hidden]': '!selectable' },
  template: `
    <button
      *transloco="let t"
      hlmBtn
      variant="outline"
      [attr.aria-label]="t('language.label')"
      [hlmDropdownMenuTrigger]="langMenu"
      align="end"
    >
      <ng-icon name="lucideGlobe" />
      {{ shortLabel() }}
    </button>

    <ng-template #langMenu>
      <hlm-dropdown-menu sideOffset="2" class="min-w-36">
        @for (option of items(); track option.id) {
          <button hlmDropdownMenuItem (click)="select(option.id)">
            <ng-icon
              name="lucideCheck"
              data-icon="inline-start"
              [class.invisible]="!option.active"
            />
            <span>{{ option.label }}</span>
          </button>
        }
      </hlm-dropdown-menu>
    </ng-template>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LanguageSwitcher {
  private readonly languageService = inject(LanguageService);
  private readonly authService = inject(AuthService);
  private readonly settingService = inject(SettingService);
  private readonly sessionContext = inject(SessionContextService);
  private readonly transloco = inject(TranslocoService);

  /** 只有一种语言时没有可切换的，隐藏整个切换器（含它在操作区里占的位置）。 */
  protected readonly selectable = this.languageService.options.length > 1;

  /** 当前语言的短标识（EN / 中），显示在全球化图标右侧；随语言切换响应式更新。 */
  readonly shortLabel = computed(() => this.languageService.currentMeta().short);

  readonly items = computed<LangOption[]>(() => {
    const active = this.languageService.activeLang();
    return this.languageService.options.map((option) => ({
      id: option.id,
      label: option.label,
      active: option.id === active,
    }));
  });

  select(lang: Lang): void {
    // 已登录的选择属于账户：先写回设置、成功后再切换，否则设置页的重取会早于写入完成；
    // 会话建立时以账户设置为准，不写回的话下次登录就被覆盖。
    if (this.authService.isAuthenticated()) {
      this.settingService
        .setForCurrentUser({ name: SETTINGS.display.language, value: lang })
        .subscribe({
          next: () => {
            void this.languageService.applyAccountLang(lang);
            // 设置快照跟着刷新，否则偏好页与按快照解析的消费端仍停在旧值（组织默认存在时尤其明显）。
            // 刷新失败不回退：账户里已经存好了，界面也已是这门语言，下次会话建立时快照自然对齐
            this.sessionContext.refreshSettings().catch(() => undefined);
          },
          // 保存失败就不切换，并且说出来：静默吞掉的话，用户会以为账户偏好存好了。
          error: (error: unknown) =>
            toast.error(this.transloco.translate('language.saveFailed'), {
              description: applicationErrorMessage(error),
            }),
        });
      return;
    }

    // 未登录的选择属于这台设备：落盘，作为访客页面与主体离开后的回落值。
    this.languageService.setDeviceLang(lang);
  }
}
