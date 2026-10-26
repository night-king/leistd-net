import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { AccountPreferencesService } from './account-preferences-service';
import { AuthService } from './auth-service';
//#if (IncludeLocalization)
import { provideTranslocoTesting } from '../i18n/transloco.testing';
//#endif
import { SILENT_AUTH } from '../interceptors/http-context-tokens';

describe('AuthService', () => {
  let service: AuthService;
  let httpTesting: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      //#if (IncludeLocalization)
      providers: [
        AuthService,
        provideHttpClient(),
        provideHttpClientTesting(),
        // 读 /me 时按需取偏好服务，它经语言服务依赖 Transloco
        ...provideTranslocoTesting(['en', 'zh-CN']),
      ],
      //#else
      providers: [AuthService, provideHttpClient(), provideHttpClientTesting()],
      //#endif
    });
    service = TestBed.inject(AuthService);
    httpTesting = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpTesting.verify());

  // 号在 /me 发出时取：回来之前主体离开或有写入成功，这份读取就过期，建立会话时不再应用
  it('takes the preference read ticket when the /me request is sent', async () => {
    const preferences = TestBed.inject(AccountPreferencesService);
    const loading = service.initializeAuth();
    const request = httpTesting.expectOne('/api/v1/auth/me');

    await preferences.clear();
    request.flush({ id: 'u1', username: 'alice', preferences: null, preferenceOverrides: null });
    await loading;

    expect(service.lastReadPreferences()?.ticket).not.toBe(preferences.beginRead());
  });

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
