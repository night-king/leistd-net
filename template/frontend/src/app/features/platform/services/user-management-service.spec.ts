import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { UserManagementService } from './user-management-service';

describe('UserManagementService', () => {
  let service: UserManagementService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(UserManagementService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('maps the list filters to query parameters with each role as a repeated key', () => {
    service
      .getUsers({
        offset: 0,
        limit: 20,
        keyword: 'alice',
        isActive: false,
        //#if (LocalIdentity)
        isEmailVerified: true,
        //#endif
        roles: ['admin', 'auditor'],
        sorting: 'username asc',
      })
      .subscribe();

    const request = http.expectOne((req) => req.url === '/api/v1/users');
    //#if (LocalIdentity)
    expect(request.request.params.toString()).toBe(
      'offset=0&limit=20&keyword=alice&isActive=false&isEmailVerified=true' +
        '&roles=admin&roles=auditor&sorting=username%20asc',
    );
    //#else
    expect(request.request.params.toString()).toBe(
      'offset=0&limit=20&keyword=alice&isActive=false&roles=admin&roles=auditor' +
        '&sorting=username%20asc',
    );
    //#endif
    request.flush({ items: [], totalCount: 0 });
  });

  it('omits filters that are not set', () => {
    service.getUsers({ keyword: '', roles: [] }).subscribe();

    const request = http.expectOne((req) => req.url === '/api/v1/users');
    expect(request.request.params.keys()).toEqual([]);
    request.flush({ items: [], totalCount: 0 });
  });
});
