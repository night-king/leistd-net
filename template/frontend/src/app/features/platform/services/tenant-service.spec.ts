import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { TenantService } from './tenant-service';

describe('TenantService', () => {
  let service: TenantService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(TenantService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('maps the list filters to query parameters', () => {
    service.getTenants({ offset: 10, limit: 10, keyword: 'acme' }).subscribe();

    const request = http.expectOne((req) => req.url === '/api/v1/tenants');
    expect(request.request.method).toBe('GET');
    expect(request.request.params.toString()).toBe('offset=10&limit=10&keyword=acme');
    request.flush({ items: [], totalCount: 0 });
  });

  // 偏移 0 是第一页，必须照发；空关键字等于没有筛选，不发
  it('keeps a zero offset and drops an empty keyword', () => {
    service.getTenants({ offset: 0, keyword: '' }).subscribe();

    const request = http.expectOne((req) => req.url === '/api/v1/tenants');
    expect(request.request.params.toString()).toBe('offset=0');
    request.flush({ items: [], totalCount: 0 });
  });

  it('sends the activation flag as the body of the activation endpoint', () => {
    service.setActivation('tenant-1', false).subscribe();

    const request = http.expectOne({ method: 'PUT', url: '/api/v1/tenants/tenant-1/activation' });
    expect(request.request.body).toEqual({ isActive: false });
    request.flush({});
  });

  it('addresses a single tenant by id for read, update and delete', () => {
    const body = { name: 'acme', displayName: 'Acme Corp' };
    service.getTenant('tenant-1').subscribe();
    service.updateTenant('tenant-1', body).subscribe();
    service.deleteTenant('tenant-1').subscribe();

    http.expectOne({ method: 'GET', url: '/api/v1/tenants/tenant-1' }).flush({});
    const update = http.expectOne({ method: 'PUT', url: '/api/v1/tenants/tenant-1' });
    expect(update.request.body).toEqual(body);
    update.flush({});
    http.expectOne({ method: 'DELETE', url: '/api/v1/tenants/tenant-1' }).flush(null);
  });
});
