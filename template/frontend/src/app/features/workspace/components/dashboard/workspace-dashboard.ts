// prettier-ignore
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  //#if (LocalIdentity || IncludeNotifications)
  signal,
  //#endif
} from '@angular/core';
import { RouterLink } from '@angular/router';
//#if (IncludeLocalization)
// prettier-ignore
import {
  TranslocoDirective,
  //#if (IncludeNotifications)
  TranslocoService,
  //#endif
  translateSignal,
} from '@jsverse/transloco';
//#endif
import { NgIcon, provideIcons } from '@ng-icons/core';
// prettier-ignore
import {
  lucideActivity,
  lucideBell,
  lucideCheck,
  lucideChevronRight,
  lucideCog,
  lucideFileText,
  lucideInfo,
  lucideLayers,
  lucideLock,
  lucideMonitor,
  lucideSearch,
  lucideShieldAlert,
  lucideSlidersHorizontal,
  lucideSmartphone,
  lucideTablet,
  lucideTriangleAlert,
  lucideUser,
} from '@ng-icons/lucide';
//#if (IncludeNotifications)
import { toast } from '@spartan-ng/brain/sonner';
//#endif
import { HlmBadge } from '@spartan-ng/helm/badge';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmCardImports } from '@spartan-ng/helm/card';
import { HlmKbdImports } from '@spartan-ng/helm/kbd';
//#if (LocalIdentity || IncludeNotifications)
import { HlmSkeleton } from '@spartan-ng/helm/skeleton';
//#endif

//#if (IncludeNotifications)
import { applicationErrorMessage } from '../../../../core/errors/application-http-error';
//#endif
import { AuthService } from '../../../../core/services/auth-service';
import { AuthorizationService } from '../../../../core/services/authorization-service';
import { CommandPaletteService } from '../../../../core/services/command-palette-service';
import { LayoutService } from '../../../../core/services/layout-service';
//#if (IncludeNotifications)
import { NotificationService } from '../../../../core/services/notification-service';
//#endif
import { SettingContextService } from '../../../../core/settings/setting-context-service';
//#if (LocalIdentity)
import {
  TwoFactorStatusOutputDto,
  UserSessionOutputDto,
} from '../../../../shared/dtos/account-security.dto';
//#endif
//#if (LocalIdentity || IncludeNotifications)
import { AppDate } from '../../../../shared/pipes/app-date-pipe';
//#endif
//#if (!IncludeLocalization)
import { englishText } from '../../../../shared/utils/english-text';
//#endif
//#if (LocalIdentity)
import { DeviceKind, describeUserAgent } from '../../../../shared/utils/user-agent';
//#endif
//#if (LocalIdentity)
import { WorkbenchService } from '../../services/workbench-service';
//#endif

/** 工作台的一个入口：常用动作与快捷入口都用它。`label` 是词条键（不含本地化时是英文原文）。 */
interface WorkbenchLink {
  label: string;
  icon: string;
  route: string;
}
//#if (LocalIdentity)

const DEVICE_ICONS: Record<DeviceKind, string> = {
  desktop: 'lucideMonitor',
  mobile: 'lucideSmartphone',
  tablet: 'lucideTablet',
};
//#endif

/**
 * 用户工作台：问候、命令框与常用动作、继续工作（示例）、快捷入口、最近通知，以及账户安全检查与
 * 登录设备。只读汇总，写操作都跳到个人设置里完成。
 */
@Component({
  selector: 'app-workspace-dashboard',
  standalone: true,
  // prettier-ignore
  imports: [
    RouterLink,
    NgIcon,
    HlmBadge,
    HlmButton,
    ...HlmCardImports,
    ...HlmKbdImports,
    //#if (LocalIdentity || IncludeNotifications)
    HlmSkeleton,
    AppDate,
    //#endif
    //#if (IncludeLocalization)
    TranslocoDirective,
    //#endif
  ],
  // prettier-ignore
  providers: [provideIcons({
    lucideActivity, lucideBell, lucideCheck, lucideChevronRight, lucideCog, lucideFileText,
    lucideInfo, lucideLayers, lucideLock, lucideMonitor, lucideSearch, lucideShieldAlert,
    lucideSlidersHorizontal, lucideSmartphone, lucideTablet, lucideTriangleAlert, lucideUser,
  })],
  templateUrl: './workspace-dashboard.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class WorkspaceDashboard {
  private readonly layoutService = inject(LayoutService);
  private readonly authService = inject(AuthService);
  private readonly authorization = inject(AuthorizationService);
  private readonly settingContext = inject(SettingContextService);
  protected readonly commandPalette = inject(CommandPaletteService);
  protected readonly displayTimeZone = this.settingContext.timeZone;
  protected readonly displayLocale = this.settingContext.displayLocale;
  //#if (IncludeNotifications)
  protected readonly notificationService = inject(NotificationService);
  //#if (IncludeLocalization)
  private readonly transloco = inject(TranslocoService);
  //#endif
  /** 「全部标记已读」请求在途：禁用按钮，避免重复提交。 */
  readonly markingAllRead = signal(false);
  //#endif
  //#if (LocalIdentity)
  private readonly workbenchService = inject(WorkbenchService);
  //#endif
  //#if (!IncludeLocalization)
  protected readonly t = englishText(ENGLISH);
  //#endif

  readonly userName = computed(() => {
    const user = this.authService.currentUser();
    return user?.displayName || user?.username || '';
  });

  /** 按会话时区取当前小时，决定早上好 / 下午好 / 晚上好。 */
  readonly greetingPart = computed<'morning' | 'afternoon' | 'evening'>(() => {
    const hour = Number(
      new Intl.DateTimeFormat('en-US', {
        hour: 'numeric',
        hourCycle: 'h23',
        timeZone: this.settingContext.timeZone() || undefined,
      }).format(new Date()),
    );
    return hour < 12 ? 'morning' : hour < 18 ? 'afternoon' : 'evening';
  });
  //#if (LocalIdentity)

  /** null 表示尚未取回。 */
  readonly twoFactor = signal<TwoFactorStatusOutputDto | null>(null);
  readonly twoFactorFailed = signal(false);
  readonly sessions = signal<UserSessionOutputDto[] | null>(null);
  readonly sessionsFailed = signal(false);

  /**
   * 账户安全检查，只评价本项目具备的能力（不含邮件时没有邮箱验证）；两步验证状态未取回时不计入，
   * 避免把「不知道」显示成「未开启」。
   */
  readonly securityChecks = computed(() => {
    const status = this.twoFactor();
    return {
      //#if (Email)
      emailVerified: this.authService.currentUser()?.isEmailVerified === true,
      //#endif
      twoFactorEnabled: status?.enabled ?? null,
    };
  });

  readonly securityPassed = computed(() => {
    const checks = this.securityChecks();
    //#if (Email)
    return checks.emailVerified && checks.twoFactorEnabled === true;
    //#else
    return checks.twoFactorEnabled === true;
    //#endif
  });

  /** 最近的三个登录设备，当前设备在前。 */
  readonly deviceRows = computed(() =>
    (this.sessions() ?? []).slice(0, 3).map((session) => {
      const device = describeUserAgent(session.userAgent);
      return {
        id: session.id,
        isCurrent: session.isCurrent,
        icon: DEVICE_ICONS[device.kind],
        device: [device.browser, device.os].filter(Boolean).join(' · '),
        lastSeenTime: session.lastSeenTime,
      };
    }),
  );
  //#endif
  //#if (IncludeNotifications)

  /** 最近通知：与顶栏铃铛同一份共享状态，这里只取前四条。 */
  readonly recentNotifications = computed(() =>
    this.notificationService.notifications().slice(0, 4),
  );
  //#endif

  /** 命令框下方的常用动作，按形态与当前状态出现。 */
  readonly quickActions = computed<WorkbenchLink[]>(() => {
    const actions: WorkbenchLink[] = [];
    //#if (LocalIdentity)
    if (this.twoFactor()?.enabled === false) {
      actions.push({
        //#if (IncludeLocalization)
        label: 'workspace.dashboard.actions.enableTwoFactor',
        //#else
        label: 'Turn on two-factor',
        //#endif
        icon: 'lucideLock',
        route: '/workspace/settings/security',
      });
    }
    actions.push({
      //#if (IncludeLocalization)
      label: 'workspace.dashboard.actions.devices',
      //#else
      label: 'Signed-in devices',
      //#endif
      icon: 'lucideMonitor',
      route: '/workspace/settings/security',
    });
    //#endif
    actions.push({
      //#if (IncludeLocalization)
      label: 'workspace.dashboard.actions.preferences',
      //#else
      label: 'Language & time zone',
      //#endif
      icon: 'lucideSlidersHorizontal',
      route: '/workspace/settings/preferences',
    });
    //#if (IncludeNotifications)
    actions.push({
      //#if (IncludeLocalization)
      label: 'workspace.dashboard.actions.notifications',
      //#else
      label: 'Notification preferences',
      //#endif
      icon: 'lucideBell',
      route: '/workspace/settings/notifications',
    });
    //#endif
    return actions;
  });

  /** 快捷入口：个人设置的各面板、示例模块，有平台权限时加上管理平台。 */
  readonly shortcuts = computed<WorkbenchLink[]>(() => {
    const links: WorkbenchLink[] = [
      //#if (LocalIdentity)
      {
        //#if (IncludeLocalization)
        label: 'workspace.dashboard.shortcuts.profile',
        //#else
        label: 'Profile',
        //#endif
        icon: 'lucideUser',
        route: '/workspace/settings/profile',
      },
      {
        //#if (IncludeLocalization)
        label: 'workspace.dashboard.shortcuts.security',
        //#else
        label: 'Account & security',
        //#endif
        icon: 'lucideLock',
        route: '/workspace/settings/security',
      },
      //#endif
      {
        //#if (IncludeLocalization)
        label: 'workspace.dashboard.shortcuts.preferences',
        //#else
        label: 'Preferences',
        //#endif
        icon: 'lucideSlidersHorizontal',
        route: '/workspace/settings/preferences',
      },
      //#if (IncludeNotifications)
      {
        //#if (IncludeLocalization)
        label: 'workspace.dashboard.shortcuts.notifications',
        //#else
        label: 'Notifications',
        //#endif
        icon: 'lucideBell',
        route: '/workspace/settings/notifications',
      },
      //#endif
      {
        //#if (IncludeLocalization)
        label: 'workspace.dashboard.shortcuts.exampleModule',
        //#else
        label: 'Example module',
        //#endif
        icon: 'lucideLayers',
        route: '/workspace/placeholder',
      },
    ];
    if (this.authorization.canAccessPlatform()) {
      links.push({
        //#if (IncludeLocalization)
        label: 'workspace.dashboard.shortcuts.platform',
        //#else
        label: 'Admin platform',
        //#endif
        icon: 'lucideCog',
        route: '/platform',
      });
    }
    return links;
  });

  constructor() {
    //#if (IncludeLocalization)
    const title = translateSignal('workspace.dashboard.title', {}, { scope: 'workspace' });
    effect(() => this.layoutService.title.set(title()));
    //#else
    effect(() => this.layoutService.title.set('Workbench'));
    //#endif
    //#if (LocalIdentity)
    this.loadTwoFactor();
    this.loadSessions();
    //#endif
  }
  //#if (IncludeNotifications)

  /** 与铃铛共用通知状态；失败时提示原因，已读状态保持原样。 */
  async markAllNotificationsRead(): Promise<void> {
    this.markingAllRead.set(true);
    try {
      await this.notificationService.markAllAsRead();
    } catch (error: unknown) {
      //#if (IncludeLocalization)
      toast.error(this.transloco.translate('common.requestError'), {
        description: applicationErrorMessage(error),
      });
      //#else
      toast.error('Request failed', { description: applicationErrorMessage(error) });
      //#endif
    } finally {
      this.markingAllRead.set(false);
    }
  }
  //#endif
  //#if (LocalIdentity)

  loadTwoFactor(): void {
    this.twoFactorFailed.set(false);
    this.workbenchService.getTwoFactorStatus().subscribe({
      next: (status) => this.twoFactor.set(status),
      error: () => this.twoFactorFailed.set(true),
    });
  }

  loadSessions(): void {
    this.sessionsFailed.set(false);
    this.workbenchService.getSessions().subscribe({
      next: (sessions) => this.sessions.set(sessions),
      error: () => this.sessionsFailed.set(true),
    });
  }
  //#endif
}
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'workspace.dashboard.greeting.morning': 'Good morning, {{name}}',
  'workspace.dashboard.greeting.afternoon': 'Good afternoon, {{name}}',
  'workspace.dashboard.greeting.evening': 'Good evening, {{name}}',
  'workspace.dashboard.search': 'Search pages and settings…',
  'workspace.dashboard.continue.title': 'Continue where you left off',
  'workspace.dashboard.continue.note': 'Examples. Replace them with your own business modules.',
  'workspace.dashboard.continue.example': 'Example',
  'workspace.dashboard.continue.item1': 'Example module',
  'workspace.dashboard.continue.item1Hint': 'Turn this page into your business entry',
  'workspace.dashboard.continue.item2': 'Monthly report',
  'workspace.dashboard.continue.item2Hint': 'A document you were working on',
  'workspace.dashboard.continue.item3': 'Customer follow-ups',
  'workspace.dashboard.continue.item3Hint': 'A board you visit often',
  'workspace.dashboard.shortcuts.title': 'Shortcuts',
  //#if (IncludeNotifications)
  'workspace.dashboard.notifications.title': 'Recent notifications',
  'workspace.dashboard.notifications.markAllRead': 'Mark all as read',
  'workspace.dashboard.notifications.empty': 'No notifications',
  //#endif
  //#if (LocalIdentity)
  'workspace.dashboard.security.title': 'Account security',
  'workspace.dashboard.security.passed': 'All set',
  'workspace.dashboard.security.suggested': 'Could be stronger',
  //#if (Email)
  'workspace.dashboard.security.emailVerified': 'Email verified',
  'workspace.dashboard.security.emailUnverified': 'Email not verified',
  //#endif
  'workspace.dashboard.security.twoFactorOn': 'Two-factor authentication on',
  'workspace.dashboard.security.twoFactorOff': 'Two-factor authentication off',
  'workspace.dashboard.security.fix': 'Set up',
  'workspace.dashboard.devices.title': 'Signed-in devices',
  'workspace.dashboard.devices.manage': 'Manage',
  'workspace.dashboard.devices.current': 'This device',
  'workspace.dashboard.devices.unknown': 'Unknown device',
  //#endif
  'common.loadFailed': "Couldn't load the list",
  'common.retry': 'Retry',
};
//#endif
