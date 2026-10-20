// prettier-ignore
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
// prettier-ignore
import {
  //#if (IncludeNotifications)
  Router,
  //#endif
  RouterLink,
} from '@angular/router';
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
  lucideLayers,
  lucideLock,
  lucideMonitor,
  lucideSearch,
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
import { HlmTooltipImports } from '@spartan-ng/helm/tooltip';
import { interval } from 'rxjs';

//#if (IncludeNotifications)
import { applicationErrorMessage } from '../../../../core/errors/application-http-error';
//#endif
import { AuthService } from '../../../../core/services/auth-service';
import { AuthorizationService } from '../../../../core/services/authorization-service';
import { CommandPaletteService } from '../../../../core/services/command-palette-service';
import { LayoutService } from '../../../../core/services/layout-service';
//#if (IncludeNotifications)
import {
  NOTIFICATION_ICONS,
  NotificationService,
} from '../../../../core/services/notification-service';
//#endif
import { SettingContextService } from '../../../../core/settings/setting-context-service';
//#if (IncludeNotifications)
import { NotificationItem } from '../../../../shared/components/notification-item/notification-item';
//#endif
//#if (LocalIdentity || IncludeNotifications)
import { RelativeTimeLabel } from '../../../../shared/components/relative-time-label/relative-time-label';
//#endif
//#if (LocalIdentity)
import { StatusDot } from '../../../../shared/directives/status-dot';
//#endif
import { TruncatedTooltip } from '../../../../shared/directives/truncated-tooltip';
//#if (LocalIdentity)
import {
  TwoFactorStatusOutputDto,
  UserSessionOutputDto,
} from '../../../../shared/dtos/account-security.dto';
//#endif
//#if (IncludeNotifications)
import { NotificationOutputDto } from '../../../../shared/dtos/notification.dto';
import { AppNumber } from '../../../../shared/pipes/app-number-pipe';
//#endif
//#if (!IncludeLocalization)
import { englishText } from '../../../../shared/utils/english-text';
//#endif
//#if (LocalIdentity)
import { DeviceKind, describeUserAgent } from '../../../../shared/utils/user-agent';
//#endif
import { resolveDisplayTimeZone } from '../../../../shared/utils/zoned-time';
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
    ...HlmTooltipImports,
    //#if (LocalIdentity || IncludeNotifications)
    HlmSkeleton,
    RelativeTimeLabel,
    //#endif
    //#if (LocalIdentity)
    StatusDot,
    //#endif
    TruncatedTooltip,
    //#if (IncludeNotifications)
    AppNumber,
    NotificationItem,
    //#endif
    //#if (IncludeLocalization)
    TranslocoDirective,
    //#endif
  ],
  // prettier-ignore
  providers: [provideIcons({
    //#if (IncludeNotifications)
    ...NOTIFICATION_ICONS,
    //#endif
    lucideActivity, lucideBell, lucideCheck, lucideChevronRight, lucideCog, lucideFileText,
    lucideLayers, lucideLock, lucideMonitor, lucideSearch,
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
  protected readonly displayLocale = this.settingContext.displayLocale;
  /** 实际生效的展示时区：设置里的时区当前运行时用不了时回落到浏览器时区。 */
  protected readonly timeZone = computed(() =>
    resolveDisplayTimeZone(this.settingContext.timeZone()),
  );
  /** 分钟级时钟：问候语与"多久以前"随它更新，组件销毁时停止。 */
  readonly now = signal(Date.now());
  //#if (IncludeNotifications)
  protected readonly notificationService = inject(NotificationService);
  private readonly router = inject(Router);
  //#if (IncludeLocalization)
  private readonly transloco = inject(TranslocoService);
  //#endif
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

  /** 按展示时区取当前小时，决定早上好 / 下午好 / 晚上好。 */
  readonly greetingPart = computed<'morning' | 'afternoon' | 'evening'>(() => {
    const hour = Number(
      new Intl.DateTimeFormat('en-US', {
        hour: 'numeric',
        hourCycle: 'h23',
        timeZone: this.timeZone(),
      }).format(new Date(this.now())),
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

  /** 完成度圆环：通过的检查项 / 总项（有邮件能力时两项，否则一项）。两步验证未取回时由模板显示加载态。 */
  readonly securityProgress = computed(() => {
    const checks = Object.values(this.securityChecks());
    return { done: checks.filter((passed) => passed === true).length, total: checks.length };
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

  /** 已展开全文的通知，各条独立展开；摘要被截断的才有展开按钮。 */
  readonly expandedNotifications = signal<ReadonlySet<string>>(new Set());

  /** 展开 / 收起全文；展开即读完了全文，未读的同时标为已读（与铃铛面板一致）。 */
  async toggleNotification(notification: NotificationOutputDto): Promise<void> {
    const expanding = !this.expandedNotifications().has(notification.id);
    this.expandedNotifications.update((current) => {
      const next = new Set(current);
      if (!next.delete(notification.id)) {
        next.add(notification.id);
      }
      return next;
    });
    if (expanding) {
      await this.markNotificationRead(notification);
    }
  }

  /** 点一行标为已读，有链接时跳转（与铃铛面板点一条的效果一致）。 */
  async openNotification(notification: NotificationOutputDto): Promise<void> {
    await this.markNotificationRead(notification);
    if (notification.link) {
      await this.router.navigateByUrl(notification.link);
    }
  }

  /** 标记已读是附带动作：失败只提示，不阻断跳转或展开这些主动作。 */
  private async markNotificationRead(notification: NotificationOutputDto): Promise<void> {
    if (notification.isRead) return;
    try {
      await this.notificationService.markAsRead(notification.id);
    } catch (error: unknown) {
      this.showRequestError(error);
    }
  }

  /** 最近通知：与顶栏铃铛同一份共享状态，这里只取前四条。 */
  readonly recentNotifications = computed(() =>
    this.notificationService.notifications().slice(0, 4),
  );
  //#endif

  /**
   * "继续工作"的示例卡：封面色取图表序列色（类名写全，Tailwind 才扫得到）。下游项目换成自己最近处理的业务对象。
   */
  protected readonly continueExamples = [
    {
      title: 'workspace.dashboard.continue.item1',
      hint: 'workspace.dashboard.continue.item1Hint',
      icon: 'lucideLayers',
      tone: 'from-chart-1/20 text-chart-1',
    },
    {
      title: 'workspace.dashboard.continue.item2',
      hint: 'workspace.dashboard.continue.item2Hint',
      icon: 'lucideFileText',
      tone: 'from-chart-2/20 text-chart-2',
    },
    {
      title: 'workspace.dashboard.continue.item3',
      hint: 'workspace.dashboard.continue.item3Hint',
      icon: 'lucideActivity',
      tone: 'from-chart-4/20 text-chart-4',
    },
  ] as const;

  /** 命令框下方的常用动作，按形态与当前状态出现；去向页面的入口在下方快捷入口里，不重复列。 */
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
    return actions;
  });

  /** 快捷入口：个人设置的各面板，有平台权限时加上管理平台（示例模块已在"继续工作"里）。 */
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
    const destroyRef = inject(DestroyRef);
    interval(60_000)
      .pipe(takeUntilDestroyed(destroyRef))
      .subscribe(() => this.now.set(Date.now()));
    // 欢迎区自带搜索框，顶栏的同一个入口在这一页收起，避免两个搜索入口上下并列
    this.layoutService.pageHasSearch.set(true);
    destroyRef.onDestroy(() => this.layoutService.pageHasSearch.set(false));
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

  private showRequestError(error: unknown): void {
    //#if (IncludeLocalization)
    toast.error(this.transloco.translate('common.requestError'), {
      description: applicationErrorMessage(error),
    });
    //#else
    toast.error('Request failed', { description: applicationErrorMessage(error) });
    //#endif
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
  'workspace.dashboard.search': 'Search pages…',
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
  'workspace.dashboard.notifications.empty': 'No notifications',
  //#endif
  //#if (LocalIdentity)
  'workspace.dashboard.security.title': 'Account security',
  'workspace.dashboard.security.passed': 'All set',
  'workspace.dashboard.security.suggested': 'Could be stronger',
  'workspace.dashboard.security.progress': '{{done}} of {{total}} checks passed',
  //#if (Email)
  'workspace.dashboard.security.emailVerified': 'Email verified',
  'workspace.dashboard.security.emailUnverified': 'Email not verified',
  //#endif
  'workspace.dashboard.security.twoFactorOn': 'Two-factor on',
  'workspace.dashboard.security.twoFactorOff': 'Two-factor off',
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
