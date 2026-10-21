//#if (LocalIdentity)
import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';

import { TenantContextService } from '../services/tenant-context-service';
import { TENANT_HEADER } from '../tenancy/tenant-protocol';

const TENANT_PROBE_PATHS = ['/api/v1/tenants/by-host'] as const;

/**
 * 已选租户时为所有 /api/ 请求附加租户提示头（TENANT_HEADER）。
 *
 * 已登录用户的租户由服务端 cookie claim 定案，此头只影响匿名请求
 * （典型是登录：决定凭据在哪个租户内校验）。
 */
export const tenantInterceptor: HttpInterceptorFn = (req, next) => {
  if (!req.url.startsWith('/api/')) {
    return next(req);
  }

  // 租户探测端点是宿主级匿名查询，一律不附租户头：本地记着的租户失效后，带着它的探测会先被租户解析
  // 拒掉，而登录页在探测失败时锁住租户区，重试仍带同一个头，形成死锁。
  if (TENANT_PROBE_PATHS.some((path) => req.url.startsWith(path))) {
    return next(req);
  }

  const tenant = inject(TenantContextService).current();
  if (!tenant) {
    return next(req);
  }

  return next(req.clone({ setHeaders: { [TENANT_HEADER]: tenant.key } }));
};
//#endif
