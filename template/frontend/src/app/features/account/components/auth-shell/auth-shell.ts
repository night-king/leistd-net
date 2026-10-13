import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { RouterLink } from '@angular/router';
//#if (IncludeLocalization)
import { TranslocoDirective } from '@jsverse/transloco';
//#endif
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideLayers, lucideShieldCheck, lucideUsers } from '@ng-icons/lucide';

//#if (IncludeLocalization)
import { LanguageSwitcher } from '../../../../core/components/language-switcher/language-switcher';
//#endif
import { ThemeModeToggle } from '../../../../core/components/theme-mode-toggle/theme-mode-toggle';
import { Logo } from '../../../../shared/components/logo/logo';
//#if (!IncludeLocalization)
import { englishText } from '../../../../shared/utils/english-text';
//#endif

/**
 * 认证页（登录、注册、强制启用两步验证、退出确认）共用的外壳：五五分栏，左栏是独立贴边的品牌面板
 * （标题、导语与亮点居中；lg 以下隐藏，品牌标识移到表单栏顶部），右栏是主题与语言切换和居中的表单。表单之外的补充信息投影到带
 * `authShellFooter` 属性的元素。
 */
@Component({
  selector: 'app-auth-shell',
  // prettier-ignore
  imports: [
    RouterLink,
    NgIcon,
    Logo,
    ThemeModeToggle,
    //#if (IncludeLocalization)
    LanguageSwitcher,
    TranslocoDirective,
    //#endif
  ],
  providers: [provideIcons({ lucideLayers, lucideShieldCheck, lucideUsers })],
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
  protected readonly highlights = [
    {
      icon: 'lucideShieldCheck',
      title: 'account.brand.highlights.security.title',
      description: 'account.brand.highlights.security.description',
    },
    {
      icon: 'lucideUsers',
      title: 'account.brand.highlights.access.title',
      description: 'account.brand.highlights.access.description',
    },
    {
      icon: 'lucideLayers',
      title: 'account.brand.highlights.fullStack.title',
      description: 'account.brand.highlights.fullStack.description',
    },
  ] as const;
}
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'account.brand.title': 'Template Project',
  'account.brand.headline': 'Your team, permissions and data in one console',
  'account.brand.subtitle': 'Full-stack development template based on Angular + .NET',
  'account.brand.highlights.security.title': 'Secure sign-in',
  'account.brand.highlights.security.description':
    'Password policy, lockout and two-factor authentication out of the box',
  'account.brand.highlights.access.title': 'Roles and permissions',
  'account.brand.highlights.access.description':
    'Grant permissions by role; menus and APIs follow the same rules',
  'account.brand.highlights.fullStack.title': 'Full stack, end to end',
  'account.brand.highlights.fullStack.description':
    'Angular and .NET wired together, ready to deploy from day one',
  'account.brand.note': 'Open source · Self-hosted · Front end and back end included',
};
//#endif
