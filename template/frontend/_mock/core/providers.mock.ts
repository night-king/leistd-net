import { HttpInterceptorFn } from '@angular/common/http';
import { Provider } from '@angular/core';
import { finalize } from 'rxjs/operators';

import { MOCK_APIS, mockInterceptor } from './interceptor';
import { isMockedUrl, shouldProvideMock } from './matching';
import { MockConfig } from './models';
import { MOCKED_URL } from '../../src/app/core/mock/mocked-url';
import { persistMockUsers, restoreMockUsers } from '../data/user';
import * as allApis from '../index';

/** 每个请求处理完把用户数据写回本页 sessionStorage：刷新与整页跳转（退出登录）后改动仍在，与设置一致。 */
const persistMockUsersInterceptor: HttpInterceptorFn = (req, next) =>
  next(req).pipe(finalize(persistMockUsers));

/** 提供 Mock 服务，返回可直接放进 app.config.ts providers 的 Provider 数组。 */
export function provideMock(config: boolean | MockConfig): Provider[] {
  if (!shouldProvideMock(config)) {
    return [];
  }

  restoreMockUsers();
  const apis = Object.values(allApis)
    .filter((value) => typeof value === 'object' && value !== null)
    .reduce((acc, current) => ({ ...acc, ...current }), {});

  return [
    { provide: MOCK_APIS, useValue: apis },
    { provide: MOCKED_URL, useValue: (url: string) => isMockedUrl(config, url) },
  ];
}

/** 按配置返回 Mock 拦截器，供 app.config.ts 的 withInterceptors 展开。 */
export function mockInterceptors(config: boolean | MockConfig): HttpInterceptorFn[] {
  // 写回的拦截器在外层：Mock 处理器改完数据、响应结束后才写
  return shouldProvideMock(config) ? [persistMockUsersInterceptor, mockInterceptor] : [];
}
