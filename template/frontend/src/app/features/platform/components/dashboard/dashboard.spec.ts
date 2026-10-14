import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { Observable, Subject, of, throwError } from 'rxjs';

import { Dashboard } from './dashboard';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../../../core/i18n/transloco.testing';
//#endif
import { AuthorizationService } from '../../../../core/services/authorization-service';
import { StartupService } from '../../../../core/services/startup-service';
import { SettingContextService } from '../../../../core/settings/setting-context-service';
import { PERMISSIONS } from '../../../../shared/constants/permission.constants';
import { PlatformStatisticsOutputDto, StatisticsQuery } from '../../dtos/platform-statistics.dto';
//#if (IncludeOperationRecords)
import { OperationRecordService } from '../../services/operation-record-service';
//#endif
import { PlatformStatisticsService } from '../../services/platform-statistics-service';

/**
 * 仪表盘的区间、加载状态与权限：统计要 `App.Dashboard`，没有时不发请求；区间写在 URL 上，带实际生效的
 * 展示时区请求；首次失败给重试，已有数据时刷新失败保留原内容并按它自己的区间标注；只保留最后一次请求。
 */
describe('Dashboard', () => {
  /** 2026-10-13 12:00 UTC；展示时区设为 UTC，今天就是 10-13。 */
  const NOW = Date.parse('2026-10-13T12:00:00Z');
  let fixture: ComponentFixture<Dashboard>;
  let timeZone: ReturnType<typeof signal<string | null>>;
  let getStatistics: ReturnType<
    typeof vi.fn<(query: StatisticsQuery) => Observable<PlatformStatisticsOutputDto>>
  >;
  //#if (IncludeOperationRecords)
  let getOperationRecords: ReturnType<typeof vi.fn>;
  //#endif

  function statistics(query: StatisticsQuery, newUsers = 3): PlatformStatisticsOutputDto {
    const days =
      (Date.parse(`${query.endDate}T00:00:00Z`) - Date.parse(`${query.startDate}T00:00:00Z`)) /
        86_400_000 +
      1;
    const buckets = Array.from({ length: days }, (_, index) =>
      new Date(Date.parse(`${query.startDate}T00:00:00Z`) + index * 86_400_000).toISOString(),
    );
    const series = (value: number) => buckets.map(() => value);
    return {
      range: {
        startDate: query.startDate,
        endDate: query.endDate,
        timeZone: query.timeZone,
        granularity: days === 1 ? 'hour' : 'day',
        start: buckets[0],
        end: new Date(NOW).toISOString(),
        previousStart: buckets[0],
        previousEnd: buckets[0],
      },
      buckets,
      totalUsers: { value: 10, previous: 8, series: series(10) },
      newUsers: { value: newUsers, previous: 0, series: series(1) },
      //#if (IncludeOperationRecords)
      activeUsers: { value: 6, previous: 6, series: series(2) },
      //#else
      activeUsers: { value: 6, previous: null, series: null },
      //#endif
      //#if (IncludeOperationRecords && LocalIdentity)
      succeededLogins: { value: 40, previous: 20, series: series(5) },
      failedLoginAlerts: { value: 2, previous: 4, series: series(1) },
      //#endif
      //#if (LocalIdentity)
      attention: { lockedUsers: 1, adminsWithoutTwoFactor: 2, unconfirmedEmailUsers: 0 },
      //#else
      attention: {},
      //#endif
    };
  }

  async function setup(permissions: string[], url = '/'): Promise<Dashboard> {
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
        {
          provide: SettingContextService,
          useValue: { timeZone, displayLocale: signal('en') },
        },
      ],
    }).compileComponents();

    TestBed.inject(AuthorizationService).setPermissions({
      permissions,
      isSuperAdmin: false,
      versionToken: 'r1',
    });
    await TestBed.inject(Router).navigateByUrl(url);
    fixture = TestBed.createComponent(Dashboard);
    fixture.detectChanges();
    await fixture.whenStable();
    return fixture.componentInstance;
  }
  //#if (IncludeOperationRecords && LocalIdentity)

  async function navigate(url: string): Promise<void> {
    await TestBed.inject(Router).navigateByUrl(url);
    fixture.detectChanges();
    await fixture.whenStable();
  }
  //#endif

  const query = (testId: string): HTMLElement | null =>
    fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);

  const lastQuery = () => getStatistics.mock.calls.at(-1)?.[0];

  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(NOW);
    timeZone = signal<string | null>('UTC');
    getStatistics = vi.fn((statisticsQuery: StatisticsQuery) => of(statistics(statisticsQuery)));
    //#if (IncludeOperationRecords)
    getOperationRecords = vi.fn(() => of({ items: [], totalCount: 0 }));
    //#endif
  });

  afterEach(() => {
    fixture?.destroy();
    vi.useRealTimers();
  });

  it('shows no statistics and sends no request without the dashboard permission', async () => {
    await setup([PERMISSIONS.users.default]);

    expect(getStatistics).not.toHaveBeenCalled();
    expect(query('dashboard-range')).toBeNull();
    expect(query('kpi-totalUsers')).toBeNull();
  });

  it('loads the last 7 days in the display time zone by default', async () => {
    const dashboard = await setup([PERMISSIONS.dashboard.default]);

    expect(getStatistics).toHaveBeenCalledExactlyOnceWith({
      startDate: '2026-10-07',
      endDate: '2026-10-13',
      timeZone: 'UTC',
    });
    expect(dashboard.preset()).toBe('7d');
    expect(dashboard.summary()).toMatchObject({ preset: '7d', days: 7 });
  });

  it('asks with the browser time zone when the configured one cannot be used', async () => {
    timeZone.set('Mars/Olympus');

    await setup([PERMISSIONS.dashboard.default]);

    expect(lastQuery()?.timeZone).toBe(new Intl.DateTimeFormat().resolvedOptions().timeZone);
  });

  it('keeps the current preset selected when it is clicked again', async () => {
    const dashboard = await setup([PERMISSIONS.dashboard.default]);
    const current = fixture.nativeElement.querySelector(
      '[data-testid="dashboard-range"] [data-state="on"]',
    ) as HTMLButtonElement;

    current.click();
    fixture.detectChanges();

    expect(current.getAttribute('data-state')).toBe('on');
    expect(dashboard.preset()).toBe('7d');
    expect(getStatistics).toHaveBeenCalledOnce();
  });

  it('writes a preset to the URL and asks for that range', async () => {
    const dashboard = await setup([PERMISSIONS.dashboard.default]);

    dashboard.setPreset('today');
    await fixture.whenStable();

    expect(TestBed.inject(Router).url).toContain('startDate=2026-10-13&endDate=2026-10-13');
    expect(lastQuery()).toMatchObject({ startDate: '2026-10-13', endDate: '2026-10-13' });
    expect(dashboard.preset()).toBe('today');
  });

  it('restores a custom range from the URL and selects no preset', async () => {
    const dashboard = await setup(
      [PERMISSIONS.dashboard.default],
      '/?startDate=2026-09-01&endDate=2026-09-30',
    );

    expect(lastQuery()).toMatchObject({ startDate: '2026-09-01', endDate: '2026-09-30' });
    expect(dashboard.preset()).toBeNull();
  });

  it.each([
    '/?startDate=2026-10-01',
    '/?startDate=2026-10-10&endDate=2026-10-01',
    '/?startDate=2026-10-01&endDate=2026-10-14',
    '/?startDate=2025-01-01&endDate=2026-10-13',
    '/?startDate=2026-02-30&endDate=2026-03-01',
  ])('falls back to the last 7 days for an unusable URL range %s', async (url) => {
    await setup([PERMISSIONS.dashboard.default], url);

    expect(lastQuery()).toMatchObject({ startDate: '2026-10-07', endDate: '2026-10-13' });
  });

  it('takes the picked calendar days as they are and goes back to 7 days when cleared', async () => {
    const dashboard = await setup([PERMISSIONS.dashboard.default]);

    // 选择器给的是浏览器本地 Date，年月日就是选中的日历日，不经 UTC 换算
    dashboard.onRangePicked([new Date(2026, 8, 1), new Date(2026, 8, 15)]);
    await fixture.whenStable();
    expect(lastQuery()).toMatchObject({ startDate: '2026-09-01', endDate: '2026-09-15' });

    dashboard.onRangePicked(null);
    await fixture.whenStable();
    expect(lastQuery()).toMatchObject({ startDate: '2026-10-07', endDate: '2026-10-13' });
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

  it('shows the target of a recent operation and opens one record detail at a time', async () => {
    const record = (id: string, targetName?: string) => ({
      id,
      action: 'order.shipped',
      targetId: `target-${id}`,
      targetName,
      authorizationBasis: 'App.Orders.Ship',
      outcome: 'failed' as const,
      failureCode: 'Order:Unknown',
      creationTime: new Date(NOW - 60_000).toISOString(),
    });
    getOperationRecords.mockReturnValue(
      of({ items: [record('r1', 'Order 42'), record('r2')], totalCount: 2 }),
    );
    const dashboard = await setup([
      PERMISSIONS.dashboard.default,
      PERMISSIONS.operationRecords.default,
    ]);

    // 名称优先，没有名称时取标识；未登记的动作码裸显示，句子里没有目标，补在旁边
    expect(dashboard.recentRows().map((row) => row.target)).toEqual(['Order 42', 'target-r2']);
    expect(query('dashboard-recent')?.textContent).toContain('Order 42');
    const toggles = () => [
      ...query('dashboard-recent')!.querySelectorAll<HTMLButtonElement>('button[aria-expanded]'),
    ];
    expect(query('dashboard-recent')?.querySelector('app-operation-record-detail')).toBeNull();

    toggles()[0].click();
    fixture.detectChanges();
    expect(toggles()[0].getAttribute('aria-expanded')).toBe('true');
    const detail = query('dashboard-recent')?.querySelector('app-operation-record-detail');
    expect(detail?.textContent).toContain('Order:Unknown');
    // 窄屏折叠掉的目标名称在详情里仍在，标识也在
    expect(detail?.textContent).toContain('Order 42');
    expect(detail?.textContent).toContain('target-r1');

    toggles()[1].click();
    fixture.detectChanges();
    expect(toggles().map((toggle) => toggle.getAttribute('aria-expanded'))).toEqual([
      'false',
      'true',
    ]);
  });

  it('shows anonymous actors as anonymous and keeps the impersonator visible', async () => {
    getOperationRecords.mockReturnValue(
      of({
        items: [
          {
            id: 'r1',
            action: 'auth.login.failed',
            targetId: 'someone',
            outcome: 'failed',
            creationTime: new Date(NOW - 5 * 60_000).toISOString(),
          },
          {
            id: 'r2',
            action: 'user.updated',
            targetId: 'u-1',
            outcome: 'succeeded',
            actorId: 'u-2',
            actorName: 'Ada Lovelace',
            impersonatorName: 'Support',
            creationTime: new Date(NOW - 26 * 3_600_000).toISOString(),
          },
        ],
        totalCount: 2,
      }),
    );
    const dashboard = await setup([
      PERMISSIONS.dashboard.default,
      PERMISSIONS.operationRecords.default,
    ]);

    expect(dashboard.recentRows().map((row) => [row.actor, row.initials])).toEqual([
      [undefined, '?'],
      ['Ada Lovelace', 'AL'],
    ]);
    const times = [...query('dashboard-recent')!.querySelectorAll('app-relative-time-label time')];
    expect(times[0]?.textContent?.trim()).toBe('5 minutes ago');
    //#if (IncludeLocalization)
    // 单测词条为空，文案回落成键名
    expect(times[1]?.textContent?.trim()).toBe('common.time.yesterdayAt');
    //#else
    expect(times[1]?.textContent?.trim()).toMatch(/^Yesterday /);
    //#endif
    //#if (IncludeLocalization)
    // 单测词条为空，文案回落成键名
    expect(query('dashboard-recent')?.textContent).toContain('platform.dashboard.recent.anonymous');
    expect(query('dashboard-recent')?.textContent).toContain(
      'platform.dashboard.recent.impersonatedBy',
    );
    //#else
    expect(query('dashboard-recent')?.textContent).toContain('Anonymous');
    expect(query('dashboard-recent')?.textContent).toContain('Done by Support');
    //#endif
  });
  //#endif

  it('writes large values compactly and keeps the full number for the tooltip', async () => {
    getStatistics.mockImplementationOnce((statisticsQuery) =>
      of({
        ...statistics(statisticsQuery),
        totalUsers: { value: 48_200, previous: 40_000, series: [40_000, 48_200] },
      }),
    );
    const dashboard = await setup([PERMISSIONS.dashboard.default]);

    expect(dashboard.kpis().find((kpi) => kpi.id === 'totalUsers')).toMatchObject({
      valueText: '48.2K',
      valueFull: '48,200',
      change: 0.21,
    });
  });

  it('compares with the previous range and draws a sparkline only from a series', async () => {
    const dashboard = await setup([PERMISSIONS.dashboard.default]);

    const totalUsers = dashboard.kpis().find((kpi) => kpi.id === 'totalUsers');
    // 上期 8、本期 10：变化率是比例 0.25，界面写 +25%
    expect(totalUsers).toMatchObject({ value: 10, valueText: '10', valueFull: null, change: 0.25 });
    expect(query('kpi-totalUsers')?.textContent).toContain('+25%');
    expect(totalUsers?.sparkline).not.toBeNull();
    // 上一区间为 0 时没有可比口径，不给百分比
    expect(dashboard.kpis().find((kpi) => kpi.id === 'newUsers')?.change).toBeNull();
    //#if (!IncludeOperationRecords)
    // 没有操作记录时活跃用户没有序列，不画迷你图
    expect(dashboard.kpis().find((kpi) => kpi.id === 'activeUsers')?.sparkline).toBeNull();
    //#endif
    expect(query('kpi-totalUsers')?.querySelector('button[aria-label]')).not.toBeNull();
  });

  it('keeps the comparison on one line however long the wording is', async () => {
    await setup([PERMISSIONS.dashboard.default]);
    fixture.detectChanges();

    // 完整口径放在提示里，卡片上的说法再长也只截断、不把这一行撑高
    const captions = Array.from(
      fixture.nativeElement.querySelectorAll('[data-testid="kpi-comparison"]'),
    ) as HTMLElement[];
    expect(captions.length).toBeGreaterThan(0);
    for (const caption of captions) {
      const badge = caption.parentElement!.querySelector('[hlmBadge]')!;
      expect(caption.parentElement!.getBoundingClientRect().height).toBeLessThanOrEqual(
        badge.getBoundingClientRect().height + 1,
      );
    }
  });
  //#if (IncludeOperationRecords && LocalIdentity)

  it('offers daily or weekly only for ranges of two weeks or more, weekly by default past 31 days', async () => {
    const dashboard = await setup([PERMISSIONS.dashboard.default]);
    expect([dashboard.trendView(), dashboard.canChooseTrendView()]).toEqual(['day', false]);

    await navigate('/?startDate=2026-09-14&endDate=2026-10-13');
    expect([dashboard.trendView(), dashboard.canChooseTrendView()]).toEqual(['day', true]);
    dashboard.chosenTrendView.set('week');
    expect(dashboard.trendView()).toBe('week');

    // 换区间回到该区间的默认粒度
    await navigate('/?startDate=2026-08-01&endDate=2026-10-13');
    expect(dashboard.trendView()).toBe('week');

    await navigate('/?startDate=2026-10-13&endDate=2026-10-13');
    expect([dashboard.trendView(), dashboard.canChooseTrendView()]).toEqual(['hour', false]);
  });
  //#endif
  //#if (LocalIdentity)

  it('lists attention items with a positive count, colored by severity, linked only with permission', async () => {
    const withoutUsers = await setup([PERMISSIONS.dashboard.default]);
    expect(
      withoutUsers.attention().map((item) => [item.id, item.severity, item.action, item.route]),
    ).toEqual([
      ['lockedUsers', 'destructive', 'handle', null],
      ['adminsWithoutTwoFactor', 'warning', 'review', null],
    ]);

    fixture.destroy();
    TestBed.resetTestingModule();
    const withUsers = await setup([PERMISSIONS.dashboard.default, PERMISSIONS.users.default]);
    expect(withUsers.attention()[0].route).toBe('/platform/users');
  });

  it('keeps the count of an attention item visible apart from its name', async () => {
    await setup([PERMISSIONS.dashboard.default]);
    fixture.detectChanges();

    // 名称在窄栏里会截断，数量独立成徽章，截断不会把它带走
    const rows = Array.from(query('dashboard-attention')!.querySelectorAll('li'));
    expect(rows.map((row) => row.querySelector('[hlmBadge]')?.textContent?.trim())).toEqual([
      '1',
      '2',
    ]);
  });
  //#endif

  it('keeps only the latest request when the range changes quickly', async () => {
    const first = new Subject<PlatformStatisticsOutputDto>();
    const dashboard = await setup([PERMISSIONS.dashboard.default]);
    getStatistics.mockImplementationOnce(() => first);
    dashboard.setPreset('30d');
    await fixture.whenStable();
    dashboard.setPreset('7d');
    await fixture.whenStable();

    // 30 天的响应晚于 7 天回来，也不能覆盖当前区间
    first.next(statistics({ startDate: '2026-09-14', endDate: '2026-10-13', timeZone: 'UTC' }, 99));
    expect(dashboard.statistics()?.range.startDate).toBe('2026-10-07');
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

  it('keeps the earlier data labelled with its own range when a refresh fails', async () => {
    const dashboard = await setup([PERMISSIONS.dashboard.default]);
    getStatistics.mockReturnValueOnce(throwError(() => new Error('offline')));

    dashboard.setPreset('30d');
    await fixture.whenStable();
    fixture.detectChanges();

    expect(dashboard.statistics()?.range.startDate).toBe('2026-10-07');
    expect(dashboard.statisticsDays()).toBe(7);
    expect(query('dashboard-refresh-failed')).not.toBeNull();
  });

  it('keeps comparing the same progress for kept data after the time zone moves past midnight', async () => {
    const dashboard = await setup([PERMISSIONS.dashboard.default]);
    expect(dashboard.includesToday()).toBe(true);
    getStatistics.mockReturnValueOnce(throwError(() => new Error('offline')));

    // UTC 12:00 在 +14 时区已是 10-14：新区间请求失败，保留的仍是截到此刻的 10-07 – 10-13
    timeZone.set('Pacific/Kiritimati');
    await fixture.whenStable();
    fixture.detectChanges();

    expect(lastQuery()?.endDate).toBe('2026-10-14');
    expect(dashboard.statistics()?.range.endDate).toBe('2026-10-13');
    expect(dashboard.includesToday()).toBe(true);
  });

  it('compares the whole previous range once the kept range has ended', async () => {
    getStatistics.mockImplementationOnce((statisticsQuery) =>
      of({
        ...statistics(statisticsQuery),
        range: {
          ...statistics(statisticsQuery).range,
          endDate: '2026-10-12',
          end: '2026-10-13T00:00:00Z',
        },
      }),
    );
    const dashboard = await setup([PERMISSIONS.dashboard.default]);

    expect(dashboard.includesToday()).toBe(false);
  });
});
