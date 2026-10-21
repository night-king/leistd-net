import { InjectionToken } from '@angular/core';

/**
 * 判断某个请求是否由 Mock 应答。默认恒为 `false`（部署构建与单测）；本机开发构建由 `provideMock`
 * 提供实现，业务代码因此不必引用 `_mock`。
 */
export const MOCKED_URL = new InjectionToken<(url: string) => boolean>('MOCKED_URL', {
  providedIn: 'root',
  factory: () => () => false,
});
