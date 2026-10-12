import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { RouterLink } from '@angular/router';
//#if (IncludeLocalization)
import { TranslocoDirective } from '@jsverse/transloco';
//#endif

//#if (IncludeLocalization)
import { LanguageSwitcher } from '../../../../core/components/language-switcher/language-switcher';
//#endif
import { ThemeModeToggle } from '../../../../core/components/theme-mode-toggle/theme-mode-toggle';
import { Logo } from '../../../../shared/components/logo/logo';
//#if (!IncludeLocalization)
import { englishText } from '../../../../shared/utils/english-text';
//#endif

/**
 * 认证页（登录、注册、强制启用两步验证、退出确认）共用的外壳：五五分栏，左栏是品牌、主题与语言切换
 * 和居中的表单，右栏是独立贴边的品牌面板（lg 以下隐藏）。表单之外的补充信息投影到带
 * `authShellFooter` 属性的元素。
 */
@Component({
  selector: 'app-auth-shell',
  // prettier-ignore
  imports: [
    RouterLink,
    Logo,
    ThemeModeToggle,
    //#if (IncludeLocalization)
    LanguageSwitcher,
    TranslocoDirective,
    //#endif
  ],
  templateUrl: './auth-shell.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AuthShell {
  //#if (!IncludeLocalization)
  protected readonly t = englishText(ENGLISH);

  //#endif
  /** 表单宽度：一般表单用 `default`；需要并排内容（如二维码与密钥）用 `wide`。 */
  readonly width = input<'default' | 'wide'>('default');
  /** 强制启用两步验证的受限会话写不了账户设置，那一页关掉语言切换器；主题只存本机，照常显示。 */
  readonly showLanguageSwitcher = input(true);
  protected readonly year = new Date().getFullYear();
}
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'account.brand.title': 'Template Project',
  'account.brand.headline': 'Your team, permissions and data in one console',
  'account.brand.subtitle': 'Full-stack development template based on Angular + .NET',
};
//#endif
