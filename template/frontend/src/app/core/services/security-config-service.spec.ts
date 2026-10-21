import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { SecurityConfigService } from './security-config-service';

/** 注册摘要的生命周期：进入页面时刷新，拿到的是当前策略，而不是应用启动时读到的那一份。 */
describe('SecurityConfigService', () => {
  let service: SecurityConfigService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(SecurityConfigService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  function answer(registrationEnabled: boolean): void {
    TestBed.tick();
    http.expectOne('/api/v1/auth/security-config').flush({
      registrationEnabled,
      //#if (Email)
      enableEmailVerification: false,
      emailVerificationAvailable: true,
      //#endif
    });
  }

  it('reads the current policy again when a page refreshes it', () => {
    service.refresh();
    answer(false);
    expect(service.registrationOpen()).toBe(false);

    // 管理员随后开放注册；用户在应用内再进入注册页
    service.refresh();
    answer(true);
    expect(service.registrationOpen()).toBe(true);
  });

  it('keeps showing the entry when the summary cannot be read', () => {
    service.refresh();
    TestBed.tick();
    http
      .expectOne('/api/v1/auth/security-config')
      .flush(null, { status: 503, statusText: 'Service Unavailable' });

    expect(service.config()).toBeNull();
    expect(service.registrationOpen()).toBe(true);
  });
});
