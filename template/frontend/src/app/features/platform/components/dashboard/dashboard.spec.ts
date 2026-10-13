import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Observable, Subject, of, throwError } from 'rxjs';

import { Dashboard } from './dashboard';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../../../core/i18n/transloco.testing';
//#endif
import { AuthorizationService } from '../../../../core/services/authorization-service';
import { StartupService } from '../../../../core/services/startup-service';
import { SettingContextService } from '../../../../core/settings/setting-context-service';
import { PERMISSIONS } from '../../../../shared/constants/permission.constants';
import { PlatformStatisticsOutputDto, StatisticsDays } from '../../dtos/platform-statistics.dto';
//#if (IncludeOperationRecords)
import { OperationRecordService } from '../../services/operation-record-service';
//#endif
import { PlatformStatisticsService } from '../../services/platform-statistics-service';

/**
 * 仪表盘的加载状态与权限：统计要 `App.Dashboard`，没有时不发请求；首次失败给重试，已有数据时
 * 刷新失败保留原内容；切换区间只保留最后一次请求。
 */
describe('Dashboard', () => {
  let fixture: ComponentFixture<Dashboard>;
  let getStatistics: ReturnType<
    typeof vi.fn<(days: StatisticsDays) => Observable<PlatformStatisticsOutputDto>>
  >;
  //#if (IncludeOperationRecords)
  let getOperationRecords: ReturnType<typeof vi.fn>;
  //#endif

  function statistics(days: StatisticsDays, newUsers = 3): PlatformStatisticsOutputDto {
    return {
      days,
      totalUsers: { value: 10, previous: 8 },
      newUsers: { value: newUsers, previous: 0 },
      activeUsers: { value: 6, previous: 6 },
      //#if (IncludeOperationRecords && LocalIdentity)
      failedLogins: { value: 2, previous: 4 },
      loginTrend: [{ date: '2026-10-12', succeeded: 5, failed: 1 }],
      //#endif
      //#if (LocalIdentity)
      attention: { lockedUsers: 1, adminsWithoutTwoFactor: 0, unconfirmedEmailUsers: 0 },
      //#else
      attention: {},
      //#endif
    };
  }

  async function setup(permissions: string[]): Promise<Dashboard> {
    await TestBed.configureTestingModule({
      imports: [Dashboard],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        //#if (IncludeLocalization)
        ...provideTranslocoTesting(['en']),
        //#endif
        { provide: PlatformStatisticsService, useValue: { getStatistics } },
        //#if (IncludeOperationRecords)
        {
          provide: OperationRecordService,
          useValue: { getOperationRecords },
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
    fixture = TestBed.createComponent(Dashboard);
    fixture.detectChanges();
    await fixture.whenStable();
    return fixture.componentInstance;
  }

  const query = (testId: string): HTMLElement | null =>
    fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);

  beforeEach(() => {
    getStatistics = vi.fn((days: StatisticsDays) => of(statistics(days)));
    //#if (IncludeOperationRecords)
    getOperationRecords = vi.fn(() => of({ items: [], totalCount: 0 }));
    //#endif
  });

  afterEach(() => fixture?.destroy());

  it('shows no statistics and sends no request without the dashboard permission', async () => {
    await setup([PERMISSIONS.users.default]);

    expect(getStatistics).not.toHaveBeenCalled();
    expect(query('dashboard-range')).toBeNull();
    expect(query('kpi-totalUsers')).toBeNull();
  });

  it('keeps the current range selected when it is clicked again', async () => {
    const dashboard = await setup([PERMISSIONS.dashboard.default]);
    const current = fixture.nativeElement.querySelector(
      '[data-testid="dashboard-range"] [data-state="on"]',
    ) as HTMLButtonElement;

    current.click();
    fixture.detectChanges();

    expect(current.getAttribute('data-state')).toBe('on');
    expect(dashboard.days()).toBe(7);
    expect(getStatistics).toHaveBeenCalledOnce();
  });
  //#if (IncludeOperationRecords)

  it('does not request recent operations without the dashboard permission', async () => {
    await setup([PERMISSIONS.operationRecords.default]);

    expect(getOperationRecords).not.toHaveBeenCalled();
    expect(query('dashboard-recent')).toBeNull();
  });

  it('sends a single request when retrying recent operations', async () => {
    getOperationRecords.mockReturnValueOnce(throwError(() => new Error('offline')));
    const dashboard = await setup([
      PERMISSIONS.dashboard.default,
      PERMISSIONS.operationRecords.default,
    ]);
    expect(dashboard.recentRecordsFailed()).toBe(true);

    dashboard.loadRecentRecords();
    fixture.detectChanges();
    await fixture.whenStable();

    expect(getOperationRecords).toHaveBeenCalledTimes(2);
    expect(dashboard.recentRecords()).toEqual([]);
  });
  //#endif

  it('loads the last 7 days and compares with the previous period', async () => {
    const dashboard = await setup([PERMISSIONS.dashboard.default]);

    expect(getStatistics).toHaveBeenCalledExactlyOnceWith(7);
    const totalUsers = dashboard.kpis().find((kpi) => kpi.id === 'totalUsers');
    expect(totalUsers).toMatchObject({ value: 10, change: 25 });
    // 上一区间为 0 时没有可比口径，不给百分比
    expect(dashboard.kpis().find((kpi) => kpi.id === 'newUsers')?.change).toBeNull();
    expect(query('kpi-totalUsers')).not.toBeNull();
  });
  //#if (LocalIdentity)

  it('lists only attention items with a positive count', async () => {
    const dashboard = await setup([PERMISSIONS.dashboard.default]);

    expect(dashboard.attention().map((item) => item.id)).toEqual(['lockedUsers']);
  });
  //#endif

  it('keeps only the latest request when the range changes quickly', async () => {
    const first = new Subject<PlatformStatisticsOutputDto>();
    const dashboard = await setup([PERMISSIONS.dashboard.default]);
    getStatistics.mockImplementationOnce(() => first);
    dashboard.setDays(30);
    dashboard.setDays(7);

    // 30 天的响应晚于 7 天回来，也不能覆盖当前区间
    first.next(statistics(30, 99));
    expect(dashboard.statistics()?.days).toBe(7);
  });

  it('offers a retry when the first load fails', async () => {
    getStatistics.mockReturnValueOnce(throwError(() => new Error('offline')));
    const dashboard = await setup([PERMISSIONS.dashboard.default]);

    expect(query('dashboard-load-failed')).not.toBeNull();

    dashboard.loadStatistics();
    fixture.detectChanges();
    expect(dashboard.statistics()).not.toBeNull();
    expect(query('dashboard-load-failed')).toBeNull();
  });

  it('keeps the earlier data and says so when a refresh fails', async () => {
    const dashboard = await setup([PERMISSIONS.dashboard.default]);
    getStatistics.mockReturnValueOnce(throwError(() => new Error('offline')));

    dashboard.setDays(30);
    fixture.detectChanges();

    expect(dashboard.statistics()?.days).toBe(7);
    expect(query('dashboard-refresh-failed')).not.toBeNull();
  });
});
