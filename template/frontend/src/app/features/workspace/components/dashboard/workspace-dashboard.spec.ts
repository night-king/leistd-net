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
//#if (IncludeNotifications)
import { NotificationService } from '../../../../core/services/notification-service';
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
  //#endif

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
