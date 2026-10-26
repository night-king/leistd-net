import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { Router, provideRouter } from '@angular/router';
//#if (IncludeLocalization)
import { TranslocoService } from '@jsverse/transloco';
//#endif
import { toast } from '@spartan-ng/brain/sonner';
import { PaginationState } from '@tanstack/angular-table';
import { Observable, of, Subject, throwError } from 'rxjs';

import { OperationRecords } from './operation-records';
import { OperationRecordTable } from './widgets/operation-record-table/operation-record-table';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../../../../core/i18n/transloco.testing';
//#endif
import { AccountPreferencesService } from '../../../../core/services/account-preferences-service';
import { AuthorizationService } from '../../../../core/services/authorization-service';
import { FacetedFilter } from '../../../../shared/components/faceted-filter/faceted-filter';
import { PERMISSIONS } from '../../../../shared/constants/permission.constants';
import {
  ExportOperationRecordsInputDto,
  GetOperationRecordsInputDto,
  OperationRecordFilterOptionsDto,
} from '../../dtos/operation-record.dto';
import { OperationRecordService } from '../../services/operation-record-service';

import type { Mock } from 'vitest';

/**
 * 操作记录页：URL 查询参数到列表与导出请求的映射，以及时间区间按展示时区换算。
 * 经子组件 output 与真实路由驱动，覆盖模板绑定与 URL 往返。
 */
describe('OperationRecords', () => {
  const page = '/platform/operation-records';

  /**
   * 展示时区刻意取与浏览器时区不同的一个：两者混用的换算错误只在这种情况下显形。
   * 机器本身在东八区时改用纽约，期望值随之切换。
   */
  const browserInShanghaiOffset = new Date(2026, 2, 1).getTimezoneOffset() === -480;
  const displayZone = browserInShanghaiOffset ? 'America/New_York' : 'Asia/Shanghai';
  /** 2026-03-01 ~ 2026-03-03 在展示时区里的 UTC 起止。 */
  const pickedBounds = browserInShanghaiOffset
    ? { start: '2026-03-01T05:00:00.000Z', end: '2026-03-04T04:59:59.999Z' }
    : { start: '2026-02-28T16:00:00.000Z', end: '2026-03-03T15:59:59.999Z' };

  // 动作码只用于联动与映射，取合成值：断言不绑定任何真实登记的动作
  const filterOptions: OperationRecordFilterOptionsDto = {
    categories: ['alpha', 'beta'],
    actions: [
      { code: 'alpha.created', category: 'alpha', severity: 'info' },
      { code: 'alpha.deleted', category: 'alpha', severity: 'critical' },
      { code: 'beta.updated', category: 'beta', severity: 'notice' },
    ],
  };

  let fixture: ComponentFixture<OperationRecords>;
  let component: OperationRecords;
  let router: Router;
  let service: {
    getOperationRecords: Mock<(input: GetOperationRecordsInputDto) => Observable<unknown>>;
    getFilterOptions: Mock<() => Observable<OperationRecordFilterOptionsDto>>;
    exportOperationRecords: Mock<(input: ExportOperationRecordsInputDto) => Observable<Blob>>;
  };

  function emptyPage() {
    return of({ items: [], totalCount: 0 });
  }

  function failure() {
    return throwError(
      () => new HttpErrorResponse({ status: 400, statusText: 'Bad Request', error: {} }),
    );
  }

  function lastQuery(): GetOperationRecordsInputDto {
    const query = service.getOperationRecords.mock.calls.at(-1)?.[0];
    if (!query) {
      throw new Error('The list request was never sent');
    }

    return query;
  }

  /** 真实的子表实例：错误态与重试经它的 input/output 往返。 */
  function recordTable(): OperationRecordTable {
    return fixture.debugElement.query(By.directive(OperationRecordTable))
      .componentInstance as OperationRecordTable;
  }

  /** 三个筛选器按模板顺序：类别、动作、结果。 */
  function filters(): FacetedFilter[] {
    return fixture.debugElement
      .queryAll(By.directive(FacetedFilter))
      .map((element) => element.componentInstance as FacetedFilter);
  }

  function exportButton(): HTMLButtonElement | undefined {
    return Array.from(
      (fixture.nativeElement as HTMLElement).querySelectorAll<HTMLButtonElement>('button'),
    ).find((button) => button.querySelector('ng-icon[name="lucideDownload"]'));
  }

  function queryParam(name: string): string | null {
    return router.parseUrl(router.url).queryParamMap.get(name);
  }

  async function open(queryParams: Record<string, string> = {}): Promise<void> {
    await router.navigate([page], { queryParams });
    fixture = TestBed.createComponent(OperationRecords);
    component = fixture.componentInstance;
    await fixture.whenStable();
  }

  async function grant(...permissions: string[]): Promise<void> {
    TestBed.inject(AuthorizationService).setPermissions({
      permissions,
      isSuperAdmin: false,
      versionToken: 'r1',
    });
    await fixture.whenStable();
  }

  beforeEach(async () => {
    service = {
      getOperationRecords: vi.fn().mockName('OperationRecordService.getOperationRecords'),
      getFilterOptions: vi.fn().mockName('OperationRecordService.getFilterOptions'),
      exportOperationRecords: vi.fn().mockName('OperationRecordService.exportOperationRecords'),
    };
    service.getOperationRecords.mockImplementation(() => emptyPage());
    service.getFilterOptions.mockReturnValue(of(filterOptions));
    vi.spyOn(toast, 'error').mockImplementation(() => '');

    TestBed.configureTestingModule({
      imports: [OperationRecords],
      // prettier-ignore
      providers: [
        provideRouter([{ path: 'platform/operation-records', children: [] }]),
        provideHttpClient(),
        provideHttpClientTesting(),
        //#if (IncludeLocalization)
        ...provideTranslocoTesting(['en', 'zh-CN']),
        //#endif
        { provide: OperationRecordService, useValue: service },
        {
          provide: AccountPreferencesService,
          useValue: { timeZone: signal(displayZone), displayLocale: signal(undefined) },
        },
      ],
    });

    router = TestBed.inject(Router);
  });

  afterEach(() => {
    try {
      fixture?.destroy();
    } finally {
      vi.clearAllTimers();
      vi.useRealTimers();
    }
  });

  it('maps every filter in the URL to the list query with a zero-based offset', async () => {
    await open({
      keyword: 'alice',
      category: 'alpha',
      action: 'alpha.created',
      outcome: 'failed',
      startTime: '2026-02-28T16:00:00.000Z',
      endTime: '2026-03-01T15:59:59.999Z',
      page: '3',
      pageSize: '20',
    });

    // 后端接的是数组（重复键），界面一次只筛一个，包成单元素数组
    expect(lastQuery()).toEqual({
      offset: 40,
      limit: 20,
      keyword: 'alice',
      startTime: '2026-02-28T16:00:00.000Z',
      endTime: '2026-03-01T15:59:59.999Z',
      categories: ['alpha'],
      actions: ['alpha.created'],
      outcome: 'failed',
    });
    expect(component.hasActiveFilters()).toBe(true);
  });

  it('sends no filter fields when the URL has none', async () => {
    await open();

    expect(service.getOperationRecords).toHaveBeenCalledOnce();
    expect(lastQuery()).toEqual({
      offset: 0,
      limit: component.pagination().pageSize,
      keyword: undefined,
      startTime: undefined,
      endTime: undefined,
      categories: undefined,
      actions: undefined,
      outcome: undefined,
    });
    expect(component.hasActiveFilters()).toBe(false);
  });

  // 只选了时间区间也属于筛选态：否则空表会说"暂无操作记录"，让人以为系统从没记录过
  it('treats a date range alone as an active filter for the empty state', async () => {
    await open({ startTime: '2026-02-28T16:00:00.000Z', endTime: '2026-03-01T15:59:59.999Z' });
    fixture.detectChanges();

    const table = fixture.debugElement.query(By.directive(OperationRecordTable))
      .componentInstance as OperationRecordTable;
    expect(table.filtered()).toBe(true);
  });

  it('writes the picked days as display-zone day bounds in UTC and resets to page one', async () => {
    await open({ page: '4' });

    component.onRangeChange([new Date(2026, 2, 1), new Date(2026, 2, 3)]);
    await fixture.whenStable();

    // 起点取展示时区 3 月 1 日零点，终点取到 3 月 3 日最后一毫秒（后端是闭区间）
    expect(queryParam('startTime')).toBe(pickedBounds.start);
    expect(queryParam('endTime')).toBe(pickedBounds.end);
    expect(queryParam('page')).toBe('1');
    expect(lastQuery().startTime).toBe(pickedBounds.start);
    expect(lastQuery().endTime).toBe(pickedBounds.end);

    // 回填选择器的仍是当初选的那两天，而不是按浏览器时区算出的前一天
    const [from, to] = component.selectedRange()!;
    expect([from.getFullYear(), from.getMonth(), from.getDate()]).toEqual([2026, 2, 1]);
    expect([to.getFullYear(), to.getMonth(), to.getDate()]).toEqual([2026, 2, 3]);
  });

  it('removes both bounds when the range is cleared', async () => {
    await open({ startTime: '2026-02-28T16:00:00.000Z', endTime: '2026-03-01T15:59:59.999Z' });

    component.onRangeChange(null);
    await fixture.whenStable();

    expect(queryParam('startTime')).toBeNull();
    expect(queryParam('endTime')).toBeNull();
    expect(component.selectedRange()).toBeUndefined();
    expect(lastQuery().startTime).toBeUndefined();
  });

  // 只有一端时不算区间：选择器要的是 [Date, Date]，缺一端回填会得到半个区间
  it('ignores a range in the URL that has only one bound or an invalid bound', async () => {
    await open({ startTime: '2026-02-28T16:00:00.000Z' });
    expect(component.selectedRange()).toBeUndefined();

    await router.navigate([page], {
      queryParams: { startTime: 'not-a-date', endTime: '2026-03-01T15:59:59.999Z' },
    });
    await fixture.whenStable();
    expect(component.selectedRange()).toBeUndefined();
  });

  it('parses the typed range in the written form and rejects incomplete text', async () => {
    await open();

    const range = component.parseRangeInput()('2026-03-01 ~ 2026-03-03');
    expect(range?.map((date) => [date.getFullYear(), date.getMonth(), date.getDate()])).toEqual([
      [2026, 2, 1],
      [2026, 2, 3],
    ]);
    // 显示与解析同一写法：聚焦输入框后文本不能变成另一种写法
    expect(component.parseRangeInput()(component.formatRange()(range!))).toEqual(range);

    expect(component.parseRangeInput()('2026-03-01')).toBeNull();
    expect(component.parseRangeInput()('2026-03-01 ~ someday')).toBeNull();
    expect(component.formatRange()([null, null])).toBe('');
  });

  it('narrows the action options to the selected category', async () => {
    await open();
    fixture.detectChanges();

    expect(
      filters()[1]
        .options()
        .map((option) => option.value),
    ).toEqual(['alpha.created', 'alpha.deleted', 'beta.updated']);

    await router.navigate([page], { queryParams: { category: 'beta' } });
    await fixture.whenStable();
    fixture.detectChanges();

    expect(
      filters()[1]
        .options()
        .map((option) => option.value),
    ).toEqual(['beta.updated']);
  });

  // 换了类别留着上一个类别的动作，两个条件相交为空，界面显示"没有匹配"而人看不出原因
  it('clears the action and returns to page one when the category changes', async () => {
    await open({ category: 'alpha', action: 'alpha.created', page: '2' });
    fixture.detectChanges();

    filters()[0].valueChange.emit('beta');
    await fixture.whenStable();

    expect(queryParam('category')).toBe('beta');
    expect(queryParam('action')).toBeNull();
    expect(queryParam('page')).toBe('1');
    expect(lastQuery().categories).toEqual(['beta']);
    expect(lastQuery().actions).toBeUndefined();
  });

  it('does not refetch when a filter reports the value it already has', async () => {
    await open({ category: 'alpha', action: 'alpha.created', outcome: 'succeeded' });
    fixture.detectChanges();
    const requests = service.getOperationRecords.mock.calls.length;

    filters()[0].valueChange.emit('alpha');
    filters()[1].valueChange.emit('alpha.created');
    filters()[2].valueChange.emit('succeeded');
    await fixture.whenStable();

    expect(service.getOperationRecords).toHaveBeenCalledTimes(requests);
  });

  it('writes the outcome and the action filters to the URL and the query', async () => {
    await open();
    fixture.detectChanges();

    filters()[2].valueChange.emit('failed');
    await fixture.whenStable();
    filters()[1].valueChange.emit('beta.updated');
    await fixture.whenStable();

    expect(lastQuery().outcome).toBe('failed');
    expect(lastQuery().actions).toEqual(['beta.updated']);

    filters()[2].valueChange.emit(null);
    await fixture.whenStable();
    expect(queryParam('outcome')).toBeNull();
    expect(lastQuery().outcome).toBeUndefined();
  });

  // 占位文字输入后就消失，不能当标签：日期区间输入框另有读屏可见的 label
  it('labels the date range input for assistive technology', async () => {
    await open();
    fixture.detectChanges();

    const input = (fixture.nativeElement as HTMLElement).querySelector<HTMLInputElement>(
      '#filter-date-range',
    )!;
    expect(input).not.toBeNull();
    expect(input.labels?.[0]?.textContent?.trim()).toMatch(/Date range|dateRange/);
  });

  it('writes paging from the table to the URL and requests that page', async () => {
    await open();
    fixture.detectChanges();

    const table = fixture.debugElement.query(By.directive(OperationRecordTable))
      .componentInstance as OperationRecordTable;
    table.paginationChange.emit({ pageIndex: 2, pageSize: 50 } as PaginationState);
    await fixture.whenStable();

    expect(queryParam('page')).toBe('3');
    expect(lastQuery().offset).toBe(100);
    expect(lastQuery().limit).toBe(50);
  });

  it('writes the trimmed keyword to the URL after the debounce and resets to page one', async () => {
    await open({ page: '2' });

    const queries = service.getOperationRecords.mock.calls.length;
    vi.useFakeTimers({
      toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval', 'Date'],
    });
    component.onSearchQueryChange('  bob  ');
    expect(component.searchQuery()).toBe('  bob  ');
    await vi.advanceTimersByTimeAsync(299);
    expect(service.getOperationRecords.mock.calls.length).toBe(queries);
    expect(queryParam('keyword')).toBeNull();
    await vi.advanceTimersByTimeAsync(1);
    await fixture.whenStable();

    expect(queryParam('keyword')).toBe('bob');
    expect(queryParam('page')).toBe('1');
    expect(lastQuery().keyword).toBe('bob');
  });

  it('keeps the page usable with empty filters when the filter options fail to load', async () => {
    service.getFilterOptions.mockReturnValue(failure());

    await open();

    // 筛选项拿不到只让下拉为空，列表照常可用；弹错误会让人以为整页坏了
    expect(component.filterOptions()).toEqual({ categories: [], actions: [] });
    expect(toast.error).not.toHaveBeenCalled();
    expect(service.getOperationRecords).toHaveBeenCalledOnce();
  });

  it('shows a failed first load as a retryable error state, not as an empty list', async () => {
    service.getOperationRecords.mockReturnValue(failure());

    await open();

    // 没有旧行可保留：表格拿到失败原因显示错误态，不弹提示也不显示"暂无记录"
    expect(component.loading()).toBe(false);
    expect(component.records()).toEqual([]);
    expect(component.loadError()).not.toBeNull();
    expect(recordTable().loadError()).toBe(component.loadError());
    expect(toast.error).not.toHaveBeenCalled();

    const requests = service.getOperationRecords.mock.calls.length;
    service.getOperationRecords.mockImplementation(() => emptyPage());
    recordTable().retry.emit();
    await fixture.whenStable();

    expect(service.getOperationRecords).toHaveBeenCalledTimes(requests + 1);
    expect(component.loadError()).toBeNull();
  });

  it('keeps the loaded records and reports a failed refresh', async () => {
    const record = {
      id: 'r-1',
      action: 'alpha.created',
      targetId: 't-1',
      authorizationBasis: 'alpha',
      outcome: 'succeeded',
      creationTime: '2026-09-20T10:00:00Z',
    };
    service.getOperationRecords.mockReturnValue(of({ items: [record], totalCount: 1 }));
    await open();

    service.getOperationRecords.mockReturnValue(failure());
    component.reloadList();
    await fixture.whenStable();

    expect(component.records()).toHaveLength(1);
    expect(component.loadError()).toBeNull();
    expect(toast.error).toHaveBeenCalledOnce();
  });

  it('refetches the current query on refresh', async () => {
    await open({ keyword: 'alice' });
    const requests = service.getOperationRecords.mock.calls.length;

    component.reloadList();
    await fixture.whenStable();

    expect(service.getOperationRecords).toHaveBeenCalledTimes(requests + 1);
    expect(lastQuery().keyword).toBe('alice');
  });

  it('shows the export button only with the export permission', async () => {
    await open();
    await grant(PERMISSIONS.operationRecords.default);
    expect(exportButton()).toBeUndefined();

    await grant(PERMISSIONS.operationRecords.default, PERMISSIONS.operationRecords.export);
    expect(exportButton()).toBeDefined();
  });

  // 导出与列表共用同一份筛选构造：两处各拼一次，迟早导出的不是屏幕上那一批
  it('exports with the same filters as the list, without paging, and blocks a second click', async () => {
    await open({
      keyword: 'alice',
      category: 'alpha',
      action: 'alpha.created',
      outcome: 'failed',
      startTime: '2026-02-28T16:00:00.000Z',
      endTime: '2026-03-01T15:59:59.999Z',
      page: '2',
    });
    await grant(PERMISSIONS.operationRecords.default, PERMISSIONS.operationRecords.export);
    const download = new Subject<Blob>();
    service.exportOperationRecords.mockReturnValue(download);
    const createObjectUrl = vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:records');
    const revokeObjectUrl = vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => undefined);
    const click = vi
      .spyOn(HTMLAnchorElement.prototype, 'click')
      .mockImplementation(() => undefined);

    exportButton()!.click();
    await fixture.whenStable();
    expect(exportButton()!.disabled).toBe(true);
    component.onExport();

    expect(service.exportOperationRecords).toHaveBeenCalledExactlyOnceWith({
      keyword: 'alice',
      startTime: '2026-02-28T16:00:00.000Z',
      endTime: '2026-03-01T15:59:59.999Z',
      categories: ['alpha'],
      actions: ['alpha.created'],
      outcome: 'failed',
    });

    const blob = new Blob(['csv'], { type: 'text/csv' });
    download.next(blob);
    download.complete();
    await fixture.whenStable();

    expect(createObjectUrl).toHaveBeenCalledExactlyOnceWith(blob);
    expect(click).toHaveBeenCalledOnce();
    expect((click.mock.contexts[0] as HTMLAnchorElement).download).toMatch(
      /^operation-records-\d{14}\.csv$/,
    );
    expect(revokeObjectUrl).toHaveBeenCalledExactlyOnceWith('blob:records');
    expect(component.exporting()).toBe(false);
    expect(exportButton()!.disabled).toBe(false);
  });

  it('reports a failed export and allows exporting again', async () => {
    await open();
    service.exportOperationRecords.mockReturnValue(failure());
    const click = vi
      .spyOn(HTMLAnchorElement.prototype, 'click')
      .mockImplementation(() => undefined);

    component.onExport();
    await fixture.whenStable();

    expect(toast.error).toHaveBeenCalledOnce();
    expect(click).not.toHaveBeenCalled();
    expect(component.exporting()).toBe(false);

    component.onExport();
    expect(service.exportOperationRecords).toHaveBeenCalledTimes(2);
  });
  //#if (IncludeLocalization)

  // 失败原因由后端按请求语言渲染：切换语言后手里那页仍是旧语言，要重新取
  it('refetches the page when the interface language changes, but not on the initial language', async () => {
    await open();
    expect(service.getOperationRecords).toHaveBeenCalledOnce();

    TestBed.inject(TranslocoService).setActiveLang('zh-CN');
    await fixture.whenStable();

    expect(service.getOperationRecords).toHaveBeenCalledTimes(2);
  });
  //#endif
});
