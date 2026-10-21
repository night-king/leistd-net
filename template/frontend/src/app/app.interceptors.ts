import { HttpInterceptorFn } from '@angular/common/http';

import { environment } from '../environments/environment';
//#if (IncludeLocalization)
import { acceptLanguageInterceptor } from './core/interceptors/accept-language-interceptor';
//#endif
import { httpErrorInterceptor } from './core/interceptors/http-error-interceptor';
//#if (LocalIdentity && IncludeMultiTenancy)
import { tenantInterceptor } from './core/interceptors/tenant-interceptor';
//#endif
import { urlFormatInterceptor } from './core/interceptors/url-format-interceptor';
import { mockInterceptors } from '../../_mock/core/providers';

/** 应用的 HTTP 拦截器链，按顺序执行；单独导出供启动用例走同一条链。 */
export const appInterceptors: HttpInterceptorFn[] = [
  //#if (IncludeLocalization)
  // 请求语言须在 URL 改写前确定。
  acceptLanguageInterceptor,
  //#endif
  //#if (LocalIdentity && IncludeMultiTenancy)
  tenantInterceptor,
  //#endif
  urlFormatInterceptor,
  httpErrorInterceptor,
  ...mockInterceptors(environment.useMock),
];
