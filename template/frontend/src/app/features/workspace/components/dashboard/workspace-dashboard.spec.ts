import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
//#if (IncludeNotifications)
import { toast } from '@spartan-ng/brain/sonner';
//#endif
//#if (LocalIdentity)
import { Observable, of, throwError } from 'rxjs';
//#endif

import { WorkspaceDashboard } from './workspace-dashboard';
//#if (IncludeLocalization)
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

  it('opens the command palette from the search box', async () => {
    await setup();

    fixture.nativeElement.querySelector('[data-testid="workbench-search"]').click();

    expect(TestBed.inject(CommandPaletteService).isOpen()).toBe(true);
    TestBed.inject(CommandPaletteService).close();
  });
  //#if (IncludeNotifications)

  it('reports a failed mark-all-as-read and lets the user try again', async () => {
    const dashboard = await setup();
    const markAll = vi
      .spyOn(TestBed.inject(NotificationService), 'markAllAsRead')
      .mockRejectedValue(new Error('offline'));
    const error = vi.spyOn(toast, 'error');

    await dashboard.markAllNotificationsRead();

    expect(markAll).toHaveBeenCalledOnce();
    expect(error).toHaveBeenCalledOnce();
    expect(dashboard.markingAllRead()).toBe(false);
  });

  // 标为已读是附带动作：失败时提示原因，全文照常展开，通知仍是未读
  it('still expands a notification when marking it as read fails', async () => {
    const dashboard = await setup();
    vi.spyOn(TestBed.inject(NotificationService), 'markAsRead').mockRejectedValue(
      new Error('offline'),
    );
    const error = vi.spyOn(toast, 'error');
    const notification = {
      id: 'a',
      title: 'A notification',
      content: 'Its full body.',
      type: 'System',
      isRead: false,
      creationTime: new Date().toISOString(),
    };
    TestBed.inject(SignalRService).notifications.set([notification]);

    await dashboard.openNotification(notification);

    expect(dashboard.expandedNotificationId()).toBe('a');
    expect(error).toHaveBeenCalledOnce();
    expect(TestBed.inject(NotificationService).unreadCount()).toBe(1);
  });

  // 收起时是摘要，点一行展开全文并标为已读（与铃铛面板一致）；同时只展开一条
  it('expands one notification at a time and marks it as read', async () => {
    await setup();
    const markAsRead = vi
      .spyOn(TestBed.inject(NotificationService), 'markAsRead')
      .mockResolvedValue(undefined);
    const notification = (id: string) => ({
      id,
      title: `A long notification title ${id}`,
      content: `The full body of notification ${id}, longer than one line on a phone.`,
      type: 'System',
      isRead: false,
      creationTime: new Date().toISOString(),
    });
    TestBed.inject(SignalRService).notifications.set([notification('a'), notification('b')]);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    const toggles = () => [
      ...host.querySelectorAll<HTMLButtonElement>('[data-testid="workbench-notification-toggle"]'),
    ];
    const body = (id: string) =>
      host.querySelector(`#workbench-notification-${id} .text-xs`) as HTMLElement;
    expect(body('a').classList).toContain('truncate');

    toggles()[0].click();
    fixture.detectChanges();
    expect(toggles().map((toggle) => toggle.getAttribute('aria-expanded'))).toEqual([
      'true',
      'false',
    ]);
    expect(body('a').classList).not.toContain('truncate');
    expect(markAsRead).toHaveBeenCalledWith('a');

    toggles()[1].click();
    fixture.detectChanges();
    expect(toggles().map((toggle) => toggle.getAttribute('aria-expanded'))).toEqual([
      'false',
      'true',
    ]);
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
