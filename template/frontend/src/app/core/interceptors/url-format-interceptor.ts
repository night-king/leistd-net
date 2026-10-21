import {
  HttpContextToken,
  HttpEvent,
  HttpHandlerFn,
  HttpInterceptorFn,
  HttpRequest,
} from '@angular/common/http';
import { inject } from '@angular/core';
import { Observable } from 'rxjs';

import { SKIP_GATEWAY } from './http-context-tokens';
import { environment } from '../../../environments/environment';
import { MOCKED_URL } from '../mock/mocked-url';

/** 请求上下文令牌：本服务在网关中的名字。 */
export const GATEWAY_SERVICE_NAME = new HttpContextToken<string>(() => '');

/**
 * URL 格式化拦截器：为本服务的相对地址加上网关与微服务前缀并携带凭据（Cookie 会话）；
 * 绝对地址原样转发，不加凭据。
 */
export const urlFormatInterceptor: HttpInterceptorFn = (
  req: HttpRequest<unknown>,
  next: HttpHandlerFn,
): Observable<HttpEvent<unknown>> => {
  const url = req.url;

  // 由 Mock 应答的请求保持原样：Mock 按路径匹配，不加网关与服务前缀
  if (req.context.get(SKIP_GATEWAY) || isAbsoluteUrl(url) || inject(MOCKED_URL)(url)) {
    return next(req);
  }

  const gateway = environment.api.gateway || '';
  const gatewayServiceName = req.context.get(GATEWAY_SERVICE_NAME) || '';

  const pathSegments = [];
  const gatewayPart = gateway.endsWith('/') ? gateway.slice(0, -1) : gateway;
  if (gatewayPart) {
    pathSegments.push(gatewayPart);
  }
  const servicePart = gatewayServiceName.startsWith('/')
    ? gatewayServiceName.slice(1)
    : gatewayServiceName;
  if (servicePart) {
    pathSegments.push(servicePart);
  }
  pathSegments.push(url.startsWith('/') ? url.slice(1) : url);

  return next(req.clone({ url: pathSegments.join('/'), withCredentials: true }));
};

function isAbsoluteUrl(url: string): boolean {
  return /^https?:\/\//i.test(url);
}
