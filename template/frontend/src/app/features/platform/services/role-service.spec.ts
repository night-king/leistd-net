import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { RoleService } from './role-service';

describe('RoleService', () => {
  let service: RoleService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(RoleService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('maps the list filters to query parameters', () => {
    service.getRoles({ offset: 20, limit: 10, keyword: 'admin', sorting: 'name desc' }).subscribe();

    const request = http.expectOne((req) => req.url === '/api/v1/roles');
    expect(request.request.method).toBe('GET');
    expect(request.request.params.toString()).toBe(
      'offset=20&limit=10&keyword=admin&sorting=name%20desc',
    );
    request.flush({ items: [], totalCount: 0 });
  });

  // 偏移 0 是第一页，必须照发；空关键字与空排序等于没有筛选，不发
  it('keeps a zero offset and drops an empty keyword and sorting', () => {
    service.getRoles({ offset: 0, keyword: '', sorting: '' }).subscribe();

    const request = http.expectOne((req) => req.url === '/api/v1/roles');
    expect(request.request.params.toString()).toBe('offset=0');
    request.flush({ items: [], totalCount: 0 });
  });

  it('sends no query parameters without filters', () => {
    service.getRoles({}).subscribe();

    const request = http.expectOne((req) => req.url === '/api/v1/roles');
    expect(request.request.params.keys()).toEqual([]);
    request.flush({ items: [], totalCount: 0 });
  });

  it('addresses a single role by id for update and delete', () => {
    const body = {
      displayName: 'Auditor',
      description: 'Reads records',
      sort: 2,
      isDefault: false,
    };
    service.updateRole('role-1', body).subscribe();
    service.deleteRole('role-1').subscribe();

    const update = http.expectOne({ method: 'PUT', url: '/api/v1/roles/role-1' });
    expect(update.request.body).toEqual(body);
    update.flush({});
    http.expectOne({ method: 'DELETE', url: '/api/v1/roles/role-1' }).flush(null);
  });
});
