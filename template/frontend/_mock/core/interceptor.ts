import { HttpErrorResponse, HttpInterceptorFn, HttpResponse } from '@angular/common/http';
import { InjectionToken, inject } from '@angular/core';
import { from, of, throwError } from 'rxjs';
import { catchError, delay, mergeMap, tap } from 'rxjs/operators';

import { getUrlPath, isMockedUrl } from './matching';
import { MockConfig, MockException, MockRequest, MockResponse } from './models';
import { environment } from '../../src/environments/environment';

type MockApiHandler = (request: MockRequest) => unknown;
type MockApiRegistry = Record<string, MockApiHandler | unknown>;

export const MOCK_APIS = new InjectionToken<MockApiRegistry>('MOCK_APIS');

export const mockInterceptor: HttpInterceptorFn = (req, next) => {
  const apis = inject(MOCK_APIS);
  const { url, method, params, headers, body } = req;
  const mockConfig = getMockConfig();
  const matchingRule = findMatchingRule(method, url, apis);

  if (!matchingRule) {
    if (isMockedUrl(environment.useMock, url) && getUrlPath(url).startsWith('/api/')) {
      return throwError(
        () =>
          new HttpErrorResponse({
            error: {
              title: 'Mock Route Not Found',
              status: 501,
              code: 'MOCK_ROUTE_NOT_FOUND',
              detail: `Mock API is not defined: ${method.toUpperCase()} ${getUrlPath(url)}`,
            },
            headers: headers.set('Content-Type', 'application/json'),
            status: 501,
            statusText: 'Mock Route Not Found',
            url,
          }),
      );
    }

    return next(req);
  }

  if (!isMockedUrl(environment.useMock, url)) {
    return next(req);
  }

  const mockRequest: MockRequest = {
    original: req,
    url,
    queryParams: params.keys().reduce((acc, key) => ({ ...acc, [key]: params.getAll(key) }), {}),
    headers,
    body,
    params: matchingRule.urlParams,
  };

  if (mockConfig.log) {
    logMock('Mock intercepted', method, url, mockRequest);
  }

  return from(Promise.resolve().then(() => matchingRule.handler(mockRequest))).pipe(
    mergeMap((result) => {
      const mockResponse = result as MockResponse | undefined;
      const response =
        mockResponse && typeof mockResponse.status !== 'undefined'
          ? new HttpResponse(mockResponse)
          : new HttpResponse({ status: 200, body: result });

      return of(response).pipe(delay(mockResponse?.delay ?? mockConfig.delay ?? 0));
    }),
    tap((response) => {
      if (mockConfig.log) {
        logMock('Mock response for', method, url, response);
      }
    }),
    catchError((error: unknown) => {
      if (mockConfig.log) {
        logMock('Mock error for', method, url, error);
      }

      if (error instanceof MockException) {
        return throwError(
          () =>
            new HttpErrorResponse({
              error: toProblemDetails(error, getUrlPath(url)),
              headers: req.headers.set('Content-Type', 'application/problem+json'),
              status: error.status,
              statusText: 'Mock Error',
              url: req.url,
            }),
        );
      }

      return throwError(() => error);
    }),
  );
};

function findMatchingRule(
  method: string,
  url: string,
  apis: MockApiRegistry,
): { handler: MockApiHandler; urlParams: Record<string, string> } | null {
  const urlPath = getUrlPath(url);
  const exactRule = apis[`${method.toUpperCase()} ${urlPath}`];

  if (exactRule) {
    return {
      handler: typeof exactRule === 'function' ? (exactRule as MockApiHandler) : () => exactRule,
      urlParams: {},
    };
  }

  for (const [apiPattern, matchedRule] of Object.entries(apis)) {
    const [apiMethod, apiRoute] = apiPattern.split(' ');
    const match = urlPath.match(new RegExp(`^${apiRoute.replace(/:\w+/g, '([^/]+)')}$`));

    if (apiMethod !== method.toUpperCase() || !match) {
      continue;
    }

    const paramNames = (apiRoute.match(/:\w+/g) ?? []).map((name) => name.substring(1));
    const urlParams = Object.fromEntries(paramNames.map((name, index) => [name, match[index + 1]]));

    return {
      handler:
        typeof matchedRule === 'function' ? (matchedRule as MockApiHandler) : () => matchedRule,
      urlParams,
    };
  }

  return null;
}

// 后端失败统一是 RFC 9457 Problem Details（见 docs/standards/api.md「错误响应」）。Mock 里按 { code, message, errors }
// 书写，这里换成同一形状，前端走与真实后端相同的解析路径：message 进 detail，业务码进 code 扩展。
const STATUS_TITLES: Record<number, string> = {
  400: 'Bad Request',
  401: 'Unauthorized',
  403: 'Forbidden',
  404: 'Not Found',
  409: 'Conflict',
  422: 'Unprocessable Entity',
  429: 'Too Many Requests',
  500: 'Internal Server Error',
  502: 'Bad Gateway',
  503: 'Service Unavailable',
  504: 'Gateway Timeout',
};

function toProblemDetails(exception: MockException, instance: string): Record<string, unknown> {
  const payload: Record<string, unknown> =
    typeof exception.error === 'object' && exception.error !== null
      ? (exception.error as Record<string, unknown>)
      : { message: exception.error };
  const { message, code, errors, ...rest } = payload;
  const hasErrors = Array.isArray(errors) && errors.length > 0;

  return {
    type: hasErrors
      ? 'urn:leistd:problem:validation-error'
      : code
        ? 'urn:leistd:problem:business-error'
        : undefined,
    title: STATUS_TITLES[exception.status] ?? 'Error',
    status: exception.status,
    detail: typeof message === 'string' ? message : undefined,
    instance,
    ...(code ? { code } : {}),
    ...(hasErrors ? { errors } : {}),
    ...rest,
    traceId: `mock-${Date.now().toString(16)}`,
  };
}

function getMockConfig(): Partial<MockConfig> {
  const config = environment.useMock;
  return typeof config === 'boolean' ? { enable: config } : config;
}

function logMock(title: string, method: string, url: string, data: unknown): void {
  console.groupCollapsed(`${title}: ${method} ${url}`);
  console.log(data);
  console.groupEnd();
}
