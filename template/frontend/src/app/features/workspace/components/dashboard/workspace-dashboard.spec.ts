import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
// prettier-ignore
import {
  provideRouter,
  //#if (IncludeNotifications)
  Router,
  //#endif
} from '@angular/router';
//#if (IncludeLocalization)
import { TranslocoService } from '@jsverse/transloco';
//#endif
//#if (IncludeNotifications)
import { toast } from '@spartan-ng/brain/sonner';
//#endif
//#if (LocalIdentity)
import { Observable, of, throwError } from 'rxjs';
//#endif

import { WorkspaceDashboard } from './workspace-dashboard';
//#if (IncludeLocalization)
import enTexts from '../../../../../../public/i18n/en.json';
import enWorkspace from '../../../../../../public/i18n/workspace/en.json';
import { pseudoCatalog } from '../../../../core/i18n/pseudo-locale.testing';
import { provideTranslocoTesting } from '../../../../core/i18n/transloco.testing';
//#endif
import { AuthorizationService } from '../../../../core/services/authorization-service';
import { CommandPaletteService } from '../../../../core/services/command-palette-service';
import { LayoutService } from '../../../../core/services/layout-service';
//#if (IncludeNotifications)
import { NotificationService } from '../../../../core/services/notification-service';
import { SignalRService } from '../../../../core/services/signalr-service';
//#endif
import { StartupService } from '../../../../core/services/startup-service';
import { SettingContextService } from '../../../../core/settings/setting-context-service';
import { PERMISSIONS } from '../../../../shared/constants/permission.constants';
//#if (LocalIdentity)
import {
  TwoFactorStatusOutputDto,
  UserSessionOutputDto,
} from '../../../../shared/dtos/account-security.dto';
//#endif
//#if (IncludeNotifications)
import { setCoarsePointer } from '../../../../shared/utils/coarse-pointer.testing';
//#endif
//#if (IncludeLocalization)
import { parkPointer } from '../../../../shared/utils/pointer.testing';
//#endif
//#if (LocalIdentity)
import { WorkbenchService } from '../../services/workbench-service';
//#endif

/** 工作台：常用动作与快捷入口按状态和权限出现，安全检查不把「未取回」当成「未开启」，加载失败可重试。 */
describe('WorkspaceDashboard', () => {
  let fixture: ComponentFixture<WorkspaceDashboard>;
  //#if (LocalIdentity)
  let twoFactor: () => Observable<TwoFactorStatusOutputDto>;
  let sessions: () => Observable<UserSessionOutputDto[]>;
  //#endif

  async function setup(permissions: string[] = []): Promise<WorkspaceDashboard> {
    await TestBed.configureTestingModule({
      imports: [WorkspaceDashboard],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        //#if (IncludeLocalization)
        ...provideTranslocoTesting(['en']),
        //#endif
        //#if (LocalIdentity)
        {
          provide: WorkbenchService,
          useValue: { getTwoFactorStatus: () => twoFactor(), getSessions: () => sessions() },
        },
        //#endif
        { provide: StartupService, useValue: { status: signal('success' as const) } },
      ],
    }).compileComponents();

    TestBed.inject(AuthorizationService).setPermissions({
      permissions,
      isSuperAdmin: false,
      versionToken: 'r1',
    });
    // 先建好设置上下文（连带语言服务），否则首帧渲染途中创建它会让结构指令重入。
    TestBed.inject(SettingContextService);
    fixture = TestBed.createComponent(WorkspaceDashboard);
    fixture.detectChanges();
    await fixture.whenStable();
    return fixture.componentInstance;
  }

  //#if (LocalIdentity)
  beforeEach(() => {
    twoFactor = () => of({ enabled: false, recoveryCodesLeft: 0, requiredByPolicy: false });
    sessions = () => of([]);
  });

  //#endif
  afterEach(() => fixture?.destroy());
  //#if (IncludeNotifications)
  afterEach(() => setCoarsePointer(false));
  //#endif

  it('opens the command palette from the search box', async () => {
    await setup();

    fixture.nativeElement.querySelector('[data-testid="workbench-search"]').click();

    expect(TestBed.inject(CommandPaletteService).isOpen()).toBe(true);
    TestBed.inject(CommandPaletteService).close();
  });
  //#if (IncludeNotifications)

  // 标为已读是附带动作：失败时提示原因，跳转照常进行，通知仍是未读
  it('still follows the link of a notification when marking it as read fails', async () => {
    const dashboard = await setup();
    vi.spyOn(TestBed.inject(NotificationService), 'markAsRead').mockRejectedValue(
      new Error('offline'),
    );
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
    const error = vi.spyOn(toast, 'error');
    const notification = {
      id: 'a',
      title: 'A notification',
      content: 'Its full body.',
      link: '/workspace/settings',
      type: 'System',
      isRead: false,
      creationTime: new Date().toISOString(),
    };
    TestBed.inject(SignalRService).notifications.set([notification]);

    await dashboard.openNotification(notification);

    expect(navigate).toHaveBeenCalledWith('/workspace/settings');
    expect(error).toHaveBeenCalledOnce();
    expect(TestBed.inject(NotificationService).unreadCount()).toBe(1);
  });

  // 行的排版与截断由 NotificationItem 负责（见其 spec）；这里验工作台接线：各条独立展开，展开即标为已读
  it('expands clipped notifications independently and marks them read', async () => {
    await setup();
    const markAsRead = vi
      .spyOn(TestBed.inject(NotificationService), 'markAsRead')
      .mockResolvedValue(undefined);
    const notification = (id: string, content: string) => ({
      id,
      title: `Notification ${id}`,
      content,
      type: 'System',
      isRead: false,
      creationTime: new Date().toISOString(),
    });
    const long = 'Three entries could not be matched against the bank statement. '.repeat(8);
    TestBed.inject(SignalRService).notifications.set([
      notification('a', long),
      notification('b', long),
      notification('c', 'Short.'),
    ]);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    const toggles = () => [
      ...host.querySelectorAll<HTMLButtonElement>('[data-testid="notification-expand"]'),
    ];

    await expect.poll(() => toggles().length).toBe(2);

    toggles()[0].click();
    fixture.detectChanges();
    expect(toggles().map((toggle) => toggle.getAttribute('aria-expanded'))).toEqual([
      'true',
      'false',
    ]);
    expect(markAsRead).toHaveBeenCalledWith('a');

    toggles()[0].click();
    fixture.detectChanges();
    expect(markAsRead, 'collapsing does not mark again').toHaveBeenCalledOnce();

    toggles()[0].click();
    toggles()[1].click();
    fixture.detectChanges();
    expect(toggles().map((toggle) => toggle.getAttribute('aria-expanded'))).toEqual([
      'true',
      'true',
    ]);
    expect(markAsRead).toHaveBeenCalledWith('b');
    expect(
      host.querySelector('[data-testid="notification-remove"]'),
      'the workbench summary does not delete',
    ).toBeNull();
  });

  // 图标由工作台自己整组注册（测试不在父级预先提供）：漏注册不会报错，列表里只剩一块空白
  it('registers the type icons it shows', async () => {
    await setup();
    TestBed.inject(SignalRService).notifications.set([
      {
        id: 'icon',
        title: 'Sign-in from a new device',
        //#if (LocalIdentity)
        type: 'Security',
        //#else
        type: 'System',
        //#endif
        isRead: false,
        creationTime: new Date().toISOString(),
      },
    ]);
    fixture.detectChanges();

    const svg = (fixture.nativeElement as HTMLElement).querySelector(
      'app-notification-item ng-icon svg',
    );
    expect(svg, 'the icon must be registered by the dashboard itself').not.toBeNull();
    expect(svg!.getBoundingClientRect().width).toBeGreaterThan(0);
  });

  // 短标题、无正文时整行可点区域仍不小于 44px（整行由铺满的主按钮响应，时间与展开叠在它上面，所以量尺寸不点按）
  it('gives a one-line notification a 44px target on touch screens', async () => {
    await setCoarsePointer(true);
    await setup();
    TestBed.inject(SignalRService).notifications.set([
      {
        id: 'short',
        title: 'Hi',
        type: 'System',
        isRead: false,
        creationTime: new Date().toISOString(),
      },
    ]);
    fixture.detectChanges();

    const open = (fixture.nativeElement as HTMLElement).querySelector(
      '[data-testid="notification-open"]',
    )!;
    expect(open.getBoundingClientRect().height).toBeGreaterThanOrEqual(44);
  });
  //#endif

  // 欢迎区有搜索入口时顶栏收起同一个入口；离开这一页要复位，否则别的页面顶栏也没有搜索
  it('hides the header search only while the workbench is shown', async () => {
    await setup();
    const layout = TestBed.inject(LayoutService);
    expect(layout.pageHasSearch()).toBe(true);

    fixture.destroy();
    expect(layout.pageHasSearch()).toBe(false);
  });

  it('adds the admin platform shortcut only with platform access', async () => {
    const withoutAccess = await setup();
    expect(withoutAccess.shortcuts().map((link) => link.route)).not.toContain('/platform');

    fixture.destroy();
    TestBed.resetTestingModule();
    const withAccess = await setup([PERMISSIONS.users.default]);
    expect(withAccess.shortcuts().map((link) => link.route)).toContain('/platform');
  });
  //#if (IncludeLocalization)

  /** 聚焦一张卡片后有没有出现提示；失焦后收起。延时由假时钟推进到期，"没出现"即未启用。 */
  async function focusTooltip(card: HTMLElement): Promise<string | null> {
    const tooltip = () => document.querySelector<HTMLElement>('[role="tooltip"]');
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval'] });
    let text: string | null;
    try {
      card.focus();
      await vi.advanceTimersByTimeAsync(0);
      await fixture.whenStable();
      text = tooltip()?.textContent?.trim() ?? null;
      card.blur();
      await vi.advanceTimersByTimeAsync(0);
    } finally {
      vi.useRealTimers();
    }
    await expect.poll(() => tooltip()).toBeNull();
    return text;
  }

  // 伪语言按标签容量放宽文案（frontend-i18n.md §5）：卡片收到最小宽度 13rem 时标签仍单行、不撑出卡片，
  // 截断的标签聚焦时给出全文，放得下的不提示
  it('keeps pseudo-locale shortcut labels on one line at the minimum card width', async () => {
    await setup([PERMISSIONS.users.default]);
    TestBed.inject(TranslocoService).setTranslation(
      pseudoCatalog({ ...enTexts, workspace: enWorkspace }),
      'en',
    );
    fixture.detectChanges();
    await fixture.whenStable();
    const host: HTMLElement = fixture.nativeElement;
    const nav = host.querySelector<HTMLElement>('[data-testid="workbench-shortcuts"] nav')!;
    nav.style.width = '13rem';
    await new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve)));
    await fixture.whenStable();
    // 卡片布局定下来之后把残留的物理指针移开：落在哪张卡片上，它的提示就会在聚焦之前先被悬停打开
    await parkPointer();
    await expect.poll(() => document.querySelector('[role="tooltip"]')).toBeNull();

    const cards = Array.from(nav.querySelectorAll<HTMLElement>('a[hlmCard]'));
    expect(cards.length).toBeGreaterThan(0);
    for (const card of cards) {
      const label = card.querySelector<HTMLElement>('.truncate')!;
      const text = label.textContent!.trim();
      // 文案来自词条：伪语言的方括号在，写死的文案没有
      expect(text).toMatch(/^\[.+\]$/);
      expect(label.getBoundingClientRect().width, text).toBeGreaterThan(0);
      const lineHeight = Number.parseFloat(getComputedStyle(label).lineHeight);
      expect(label.getBoundingClientRect().height, text).toBeLessThan(lineHeight * 1.5);
      expect(card.scrollWidth, text).toBeLessThanOrEqual(card.clientWidth);
      const truncated = label.scrollWidth > label.clientWidth;
      expect(await focusTooltip(card), text).toBe(truncated ? text : null);
    }
    //#if (LocalIdentity)
    // 受压确实发生：最长的英文标签（Account & security）按容量放宽后，在最小宽度的卡片里放不下
    const security = nav.querySelector<HTMLElement>(
      'a[href="/workspace/settings/security"] .truncate',
    )!;
    expect(security.scrollWidth).toBeGreaterThan(security.clientWidth);
    //#endif
    expect(nav.scrollWidth).toBeLessThanOrEqual(nav.clientWidth);
  });
  //#endif
  //#if (LocalIdentity)

  it('suggests turning on two-factor only when it is known to be off', async () => {
    const dashboard = await setup();
    expect(dashboard.quickActions().map((action) => action.icon)).toContain('lucideLock');

    fixture.destroy();
    TestBed.resetTestingModule();
    twoFactor = () => of({ enabled: true, recoveryCodesLeft: 8, requiredByPolicy: false });
    const enabled = await setup();
    expect(enabled.quickActions().map((action) => action.icon)).not.toContain('lucideLock');
  });
  //#if (!Email)

  // 不含邮件时没有邮箱验证能力：安全检查不把它算作未完成，也不给去不了的修复入口
  it('evaluates only two-factor when the project has no email', async () => {
    twoFactor = () => of({ enabled: true, recoveryCodesLeft: 8, requiredByPolicy: false });
    const dashboard = await setup();

    expect(dashboard.securityPassed()).toBe(true);
    expect(
      fixture.nativeElement.querySelectorAll('[data-testid="workbench-security"] li'),
    ).toHaveLength(1);
  });
  //#endif

  it('does not report two-factor as off before its status arrives', async () => {
    twoFactor = () => throwError(() => new Error('offline'));
    const dashboard = await setup();

    expect(dashboard.securityChecks().twoFactorEnabled).toBeNull();
    expect(dashboard.securityPassed()).toBe(false);
    expect(dashboard.quickActions().map((action) => action.icon)).not.toContain('lucideLock');
  });

  it('shows how many security checks pass', async () => {
    twoFactor = () => of({ enabled: true, recoveryCodesLeft: 8, requiredByPolicy: false });
    const dashboard = await setup();

    // 测试用户的邮箱未验证：有邮件能力时两项里通过一项，没有时只评价两步验证
    //#if (Email)
    expect(dashboard.securityProgress()).toEqual({ done: 1, total: 2 });
    //#else
    expect(dashboard.securityProgress()).toEqual({ done: 1, total: 1 });
    //#endif
  });

  it('moves the relative time of devices forward with a minute clock and stops it when destroyed', async () => {
    vi.useFakeTimers({ toFake: ['Date', 'setInterval', 'clearInterval'] });
    try {
      vi.setSystemTime(Date.parse('2026-10-13T12:00:00Z'));
      sessions = () =>
        of([
          {
            id: 's1',
            creationTime: '2026-10-01T00:00:00Z',
            lastSeenTime: '2026-10-13T11:59:30Z',
            userAgent: null,
            isCurrent: true,
          },
        ]);
      const dashboard = await setup();
      const time = () =>
        (fixture.nativeElement as HTMLElement)
          .querySelector('[data-testid="workbench-devices"] app-relative-time-label time')
          ?.textContent?.trim();
      //#if (IncludeLocalization)
      // 单测词条为空，"刚刚"回落成键名
      expect(time()).toBe('common.time.justNow');
      //#else
      expect(time()).toBe('Just now');
      //#endif

      vi.advanceTimersByTime(60_000);
      fixture.detectChanges();
      expect(time()).toBe('1 minute ago');

      const before = dashboard.now();
      fixture.destroy();
      vi.advanceTimersByTime(120_000);
      expect(dashboard.now()).toBe(before);
    } finally {
      vi.useRealTimers();
    }
  });

  it('retries loading devices after a failure', async () => {
    sessions = () => throwError(() => new Error('offline'));
    const dashboard = await setup();
    expect(dashboard.sessionsFailed()).toBe(true);

    sessions = () =>
      of([
        {
          id: 's1',
          creationTime: '2026-10-01T00:00:00Z',
          lastSeenTime: '2026-10-12T00:00:00Z',
          userAgent: null,
          isCurrent: true,
        },
      ]);
    dashboard.loadSessions();

    expect(dashboard.sessionsFailed()).toBe(false);
    expect(dashboard.deviceRows()).toHaveLength(1);
  });
  //#endif
});
