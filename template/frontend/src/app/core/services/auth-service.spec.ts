import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { AuthService } from './auth-service';
import { SILENT_AUTH } from '../interceptors/http-context-tokens';

describe('AuthService', () => {
  let service: AuthService;
  let httpTesting: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [AuthService, provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(AuthService);
    httpTesting = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpTesting.verify());

  it('marks the session probe as silent so the interceptor skips the 401 redirect', async () => {
    const initialization = service.initializeAuth();
    const request = httpTesting.expectOne('/api/v1/auth/me');

    expect(request.request.context.get(SILENT_AUTH)).toBe(true);
    request.flush(null, { status: 401, statusText: 'Unauthorized' });

    await expect(initialization).rejects.toThrow();
  });

  it('propagates startup probe failures so callers can distinguish outages from 401', async () => {
    const initialization = service.initializeAuth();
    httpTesting
      .expectOne('/api/v1/auth/me')
      .flush({ detail: 'Gateway unavailable' }, { status: 503, statusText: 'Unavailable' });

    await expect(initialization).rejects.toThrow();
    expect(service.isAuthenticated()).toBe(false);
  });
});
