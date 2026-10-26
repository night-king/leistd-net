import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { TranslocoDirective, TranslocoService } from '@jsverse/transloco';
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideCheck, lucideGlobe } from '@ng-icons/lucide';
import { toast } from '@spartan-ng/brain/sonner';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmDropdownMenuImports } from '@spartan-ng/helm/dropdown-menu';

import { applicationErrorMessage } from '../../errors/application-http-error';
import { AccountPreferencesService } from '../../services/account-preferences-service';
import { AuthService } from '../../services/auth-service';
import { Lang, LanguageService } from '../../services/language-service';

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
  // 显示方式二选一：inline-flex 与 hidden 都设 display，同时存在时 Tailwind 生成顺序靠后的 inline-flex 生效
  host: {
    class: 'items-center',
    '[class.inline-flex]': 'selectable',
    '[class.hidden]': '!selectable',
  },
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
  private readonly preferences = inject(AccountPreferencesService);
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
    // 已登录的选择属于账户：写入成功后由偏好状态按响应切换；写入排在偏好队列里，
    // 与偏好面板、主题切换的写入不会乱序。
    if (this.authService.isAuthenticated()) {
      void this.preferences.write('language', lang).then((outcome) => {
        // 保存失败就不切换，并且说出来：静默吞掉的话，用户会以为账户偏好存好了。
        if (outcome.status === 'rejected') {
          toast.error(this.transloco.translate('language.saveFailed'), {
            description: applicationErrorMessage(outcome.error),
          });
        } else if (outcome.status === 'unconfirmed') {
          // 可能已经保存，不重放；下次重查账户偏好时对齐
          toast.warning(this.transloco.translate('preferences.saveUnconfirmed'), {
            description: applicationErrorMessage(outcome.error),
          });
        }
      });
      return;
    }

    // 未登录的选择属于这台设备：落盘，作为访客页面与主体离开后的回落值。
    this.languageService.setDeviceLang(lang);
  }
}
