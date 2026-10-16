import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
//#if (IncludeLocalization)
import { TranslocoDirective, TranslocoService } from '@jsverse/transloco';
//#endif
import { NgIcon, provideIcons } from '@ng-icons/core';
// prettier-ignore
import {
  lucideArrowRight,
  lucideBell,
  lucideBuilding2,
  lucideDatabase,
  lucideIdCard,
  lucideLanguages,
  lucideShieldCheck,
  lucideSunMoon,
  lucideUsers,
} from '@ng-icons/lucide';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmCardImports } from '@spartan-ng/helm/card';

//#if (IncludeLocalization)
import { LanguageSwitcher } from '../../../../core/components/language-switcher/language-switcher';
//#endif
import { ThemeModeToggle } from '../../../../core/components/theme-mode-toggle/theme-mode-toggle';
//#if (LocalIdentity)
import { SecurityConfigService } from '../../../../core/services/security-config-service';
//#endif
import { SettingContextService } from '../../../../core/settings/setting-context-service';
import { Logo } from '../../../../shared/components/logo/logo';
import { AppNumber } from '../../../../shared/pipes/app-number-pipe';
//#if (!IncludeLocalization)
import { englishText } from '../../../../shared/utils/english-text';
//#endif

/** 能力卡：按模板参数裁剪，生成项目里只列真正带上的能力。`title`、`description` 是词条键。 */
interface LandingFeature {
  icon: string;
  title: string;
  description: string;
  /** 卡片下方的示意图；`otp` 那张占约两格宽。 */
  illustration?: 'otp' | 'theme' | 'token';
}

/**
 * 着陆页：导航、首屏（标题、主次操作、产品截图）、技术栈、能力网格与收尾操作带。光晕、同心细环与
 * 渐变字只用于营销页，应用内页面不加装饰。
 */
@Component({
  selector: 'app-landing',
  standalone: true,
  // prettier-ignore
  imports: [
    RouterLink,
    NgIcon,
    HlmButton,
    ...HlmCardImports,
    Logo,
    ThemeModeToggle,
    AppNumber,
    //#if (IncludeLocalization)
    LanguageSwitcher,
    TranslocoDirective,
    //#endif
  ],
  // prettier-ignore
  providers: [provideIcons({
    lucideArrowRight, lucideBell, lucideBuilding2, lucideDatabase, lucideIdCard, lucideLanguages,
    lucideShieldCheck, lucideSunMoon, lucideUsers,
  })],
  templateUrl: './landing.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Landing {
  //#if (!IncludeLocalization)
  protected readonly t = englishText(ENGLISH);
  //#endif
  protected readonly year = new Date().getFullYear();
  protected readonly displayLocale = inject(SettingContextService).displayLocale;
  //#if (LocalIdentity)
  /** 租户关闭自助注册时不给注册入口。 */
  private readonly securityConfig = inject(SecurityConfigService);
  protected readonly registrationOpen = this.securityConfig.registrationOpen;

  constructor() {
    // 进入首页时取当前注册策略，决定是否给出注册入口
    this.securityConfig.refresh();
  }
  //#endif
  //#if (IncludeLocalization)
  private readonly transloco = inject(TranslocoService);
  //#endif
  /** 验证码示意（身份认证那张卡）：填了三位，光标停在第四格。 */
  protected readonly otpDigits = ['4', '8', '2', '', '', ''];

  protected readonly features: LandingFeature[] = [
    //#if (LocalIdentity)
    {
      icon: 'lucideShieldCheck',
      title: 'landing.features.authTitle',
      description: 'landing.features.authDesc',
      illustration: 'otp',
    },
    //#endif
    {
      icon: 'lucideUsers',
      title: 'landing.features.rbacTitle',
      description: 'landing.features.rbacDesc',
    },
    //#if (IncludeMultiTenancy)
    {
      icon: 'lucideBuilding2',
      title: 'landing.features.tenantsTitle',
      description: 'landing.features.tenantsDesc',
    },
    //#endif
    //#if (IncludeOperationRecords)
    {
      icon: 'lucideDatabase',
      title: 'landing.features.auditTitle',
      description: 'landing.features.auditDesc',
    },
    //#endif
    //#if (IncludeNotifications)
    {
      icon: 'lucideBell',
      title: 'landing.features.notificationsTitle',
      description: 'landing.features.notificationsDesc',
    },
    //#endif
    //#if (OpenIddictServer)
    {
      icon: 'lucideIdCard',
      title: 'landing.features.openAppsTitle',
      description: 'landing.features.openAppsDesc',
      illustration: 'token',
    },
    //#endif
    //#if (IncludeLocalization)
    {
      icon: 'lucideLanguages',
      title: 'landing.features.i18nTitle',
      description: 'landing.features.i18nDesc',
      illustration: 'theme',
    },
    //#else
    {
      icon: 'lucideSunMoon',
      title: 'landing.features.themeTitle',
      description: 'landing.features.themeDesc',
      illustration: 'theme',
    },
    //#endif
  ];

  /**
   * 数据条：只放这一页能核实的数字——上面列出的能力个数（随模板参数裁剪），以及运行时可切换的界面语言数。
   * 不写"几分钟跑通""测试覆盖率"这类本页无法核实的宣传数字。
   */
  protected readonly stats = computed(() => [
    { value: this.features.length, label: 'landing.stats.capabilities' },
    //#if (IncludeLocalization)
    { value: this.transloco.getAvailableLangs().length, label: 'landing.stats.languages' },
    //#endif
  ]);
}
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'layout.sidebar.appTitle': 'Template Project',
  'landing.nav.label': 'Sections',
  'landing.nav.features': 'Features',
  'landing.nav.stack': 'Tech stack',
  'landing.hero.badgeTag': 'New',
  'landing.hero.badge': 'Full-stack starter with admin console and workspace',
  'landing.hero.titleLead': 'Your team, permissions and data',
  'landing.hero.titleAccent': 'in one console',
  'landing.hero.subtitle':
    'Sign-in, roles and permissions, auditing and notifications come wired end to end. Built on Angular and .NET 10 — trim what you do not need and ship from day one.',
  'landing.hero.getStarted': 'Get started',
  'landing.hero.signIn': 'Sign in',
  'landing.hero.note': 'Open source · Self-hosted · Front end and back end included',
  'landing.hero.screenshotAlt': 'Admin console dashboard',
  'landing.stack.title': 'Built on proven open-source technology',
  'landing.features.eyebrow': 'Features',
  'landing.features.title': 'The back office essentials are already wired',
  'landing.features.subtitle':
    'You write the business logic. Sign-in, permissions and auditing come with a complete front end, back end and tests.',
  //#if (LocalIdentity)
  'landing.features.authTitle': 'Sign-in & two-factor',
  'landing.features.authDesc':
    'Password and authenticator-app sign-in with lockout, recovery codes and re-authentication sharing one policy.',
  //#endif
  'landing.features.rbacTitle': 'Roles & permissions',
  'landing.features.rbacDesc':
    'Permissions grouped by module; menus and buttons follow what each role is allowed to do.',
  //#if (IncludeMultiTenancy)
  'landing.features.tenantsTitle': 'Multi-tenancy',
  'landing.features.tenantsDesc':
    'Tenants resolved by domain or chosen at sign-in, with data isolated per tenant.',
  //#endif
  //#if (IncludeOperationRecords)
  'landing.features.auditTitle': 'Operation records',
  'landing.features.auditDesc':
    'Who did what and when — every write is recorded, filterable and exportable.',
  //#endif
  //#if (IncludeNotifications)
  'landing.features.notificationsTitle': 'Real-time notifications',
  'landing.features.notificationsDesc':
    'Notifications pushed over SignalR, scaled across instances, with history after reconnecting.',
  //#endif
  //#if (OpenIddictServer)
  'landing.features.openAppsTitle': 'Open applications',
  'landing.features.openAppsDesc':
    'Issue client credentials to other systems through a standard OpenID Connect server.',
  //#endif
  'landing.features.themeTitle': 'Light & dark themes',
  'landing.features.themeDesc':
    'Semantic color tokens with light and dark modes; rebrand by changing a few variables.',
  //#if (LocalIdentity)
  'landing.features.otpLabel': 'Enter the verification code',
  //#endif
  'landing.stats.capabilities': 'Built-in capabilities',
  'landing.cta.title': 'Start building your product',
  'landing.cta.subtitle': 'Sign in to explore the admin console and the workspace.',
  'landing.footer.tagline': 'Built with Angular, .NET and spartan/ui',
};
//#endif
