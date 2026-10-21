import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { OpenApplicationService } from './open-application-service';

describe('OpenApplicationService', () => {
  let service: OpenApplicationService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(OpenApplicationService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('maps the list filters to query parameters', () => {
    service
      .getOpenApplications({
        offset: 10,
        limit: 10,
        keyword: 'portal',
        applicationType: 'web',
        clientType: 'confidential',
        sorting: 'displayName desc',
      })
      .subscribe();

    const request = http.expectOne((req) => req.url === '/api/v1/open-applications');
    expect(request.request.params.toString()).toBe(
      'offset=10&limit=10&keyword=portal&applicationType=web&clientType=confidential' +
        '&sorting=displayName%20desc',
    );
    request.flush({ items: [], totalCount: 0 });
  });

  it('sends no query parameters without filters', () => {
    service.getOpenApplications().subscribe();

    const request = http.expectOne((req) => req.url === '/api/v1/open-applications');
    expect(request.request.params.keys()).toEqual([]);
    request.flush({ items: [], totalCount: 0 });
  });
});
