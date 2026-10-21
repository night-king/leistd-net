//#if (LocalIdentity)
import { HttpHandlerFn, HttpRequest } from '@angular/common/http';
import { Injector, provideZonelessChangeDetection, runInInjectionContext } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';

import { tenantInterceptor } from './tenant-interceptor';
import { TenantContextService } from '../services/tenant-context-service';
import { TENANT_HEADER } from '../tenancy/tenant-protocol';

/**
 * 直接以 runInInjectionContext 驱动拦截器（与 http-error-interceptor.spec 同形态），
 * next 用 of 同步回发，断言最终发出的请求头。
 */
describe('tenantInterceptor', () => {
  // 匿名流程下上下文里存的就是租户名：服务端按名字同样能解析，客户端因此不必知道租户 id
  const tenantName = 'acme';
  let injector: Injector;
  let context: TenantContextService;

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
    injector = TestBed.inject(Injector);
    context = TestBed.inject(TenantContextService);
  });

  afterEach(() => localStorage.clear());

  function send(url: string): HttpRequest<unknown> {
    let sent!: HttpRequest<unknown>;
    const next: HttpHandlerFn = (req) => {
      sent = req;
      return of();
    };

    runInInjectionContext(injector, () =>
      tenantInterceptor(new HttpRequest('GET', url), next).subscribe(),
    );
    return sent;
  }

  it('adds the tenant hint header to /api/ requests when a tenant is selected', () => {
    context.set(tenantName);

    expect(send('/api/v1/users').headers.get(TENANT_HEADER)).toBe(tenantName);
  });

  it('adds no tenant header when no tenant is selected (host)', () => {
    expect(send('/api/v1/users').headers.has(TENANT_HEADER)).toBe(false);
  });

  it('adds no tenant header to non-/api/ requests', () => {
    context.set(tenantName);

    expect(send('/assets/config.json').headers.has(TENANT_HEADER)).toBe(false);
  });

  /** 按主机名探测不附租户头：失效租户头会让探测先被拒，而登录页在探测失败时锁住租户区，形成死锁。 */
  for (const url of ['/api/v1/tenants/by-host'] as const) {
    it(`adds no tenant header to the tenant probe endpoint: ${url}`, () => {
      context.set(tenantName);

      expect(send(url).headers.has(TENANT_HEADER)).toBe(false);
    });
  }
});
//#endif
