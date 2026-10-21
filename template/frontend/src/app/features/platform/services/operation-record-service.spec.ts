import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { OperationRecordService } from './operation-record-service';

describe('OperationRecordService', () => {
  let service: OperationRecordService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(OperationRecordService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  // 服务端用 List<string> 接收，查询串要的是重复键；用 set 会只剩最后一个值且不报错
  it('sends every selected category and action as a repeated query key', () => {
    service
      .getOperationRecords({
        offset: 20,
        limit: 10,
        keyword: 'alice',
        startTime: '2026-01-01T00:00:00.000Z',
        endTime: '2026-01-31T23:59:59.999Z',
        categories: ['Users', 'Roles'],
        actions: ['Users.Create', 'Roles.Update'],
        outcome: 'failed',
      })
      .subscribe();

    const request = http.expectOne((req) => req.url === '/api/v1/operation-records');
    expect(request.request.params.toString()).toBe(
      'offset=20&limit=10&keyword=alice&startTime=2026-01-01T00:00:00.000Z' +
        '&endTime=2026-01-31T23:59:59.999Z&categories=Users&categories=Roles' +
        '&actions=Users.Create&actions=Roles.Update&outcome=failed',
    );
    request.flush({ items: [], totalCount: 0 });
  });

  it('omits filters that are not set', () => {
    service.getOperationRecords({ offset: 0, categories: [], actions: [] }).subscribe();

    const request = http.expectOne((req) => req.url === '/api/v1/operation-records');
    expect(request.request.params.toString()).toBe('offset=0');
    request.flush({ items: [], totalCount: 0 });
  });

  // 默认按 JSON 解析会在 CSV 上失败，表现为"点了没反应"
  it('exports the filtered records as a blob with the same filters and the limit', () => {
    service
      .exportOperationRecords({ categories: ['Users', 'Roles'], outcome: 'succeeded', limit: 500 })
      .subscribe();

    const request = http.expectOne((req) => req.url === '/api/v1/operation-records/export');
    expect(request.request.responseType).toBe('blob');
    expect(request.request.params.toString()).toBe(
      'categories=Users&categories=Roles&outcome=succeeded&limit=500',
    );
    request.flush(new Blob(['id\n']));
  });
});
