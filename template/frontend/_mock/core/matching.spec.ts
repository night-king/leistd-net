import { TestBed } from '@angular/core/testing';

import { isMockedUrl, shouldProvideMock } from './matching';
import * as deployed from './providers';
import * as development from './providers.mock';
import { MOCKED_URL } from '../../src/app/core/mock/mocked-url';

/**
 * Mock 的两条判定规则只有一份：是否安装 Mock、某个请求是否走 Mock。
 *
 * 登录页的演示账号、实时连接、URL 格式化都按"这个请求是否走 Mock"判定——
 * 各自用总开关近似时，只 Mock 了别的模块也会显示演示账号、断开真实的实时连接。
 */
describe('mock matching rules', () => {
  it('decides whether to install mocks from the master switch and include', () => {
    expect(shouldProvideMock(false)).toBe(false);
    expect(shouldProvideMock(true)).toBe(true);
    expect(shouldProvideMock({ enable: false })).toBe(false);
    expect(shouldProvideMock({ enable: false, include: '^/api/v1/users' })).toBe(true);
    expect(shouldProvideMock({ enable: false, include: [] })).toBe(false);
  });

  it('lets include take precedence so the master switch no longer mocks other requests', () => {
    const config = { enable: true, include: ['^/api/v1/users'] };

    expect(isMockedUrl(config, '/api/v1/users?offset=0')).toBe(true);
    expect(isMockedUrl(config, '/api/v1/roles')).toBe(false);
    expect(isMockedUrl({ enable: false, include: '^/api/v1/users' }, '/api/v1/users')).toBe(true);
  });

  it('follows the master switch without include and never mocks excluded requests', () => {
    const config = { enable: true, exclude: ['^/api/v1/settings'] };

    expect(isMockedUrl(config, '/api/v1/users')).toBe(true);
    expect(isMockedUrl(config, '/api/v1/settings')).toBe(false);
    expect(isMockedUrl({ enable: false }, '/api/v1/users')).toBe(false);
    expect(isMockedUrl(true, 'http://localhost:4200/api/v1/users')).toBe(true);
  });

  it('keeps login and realtime on the real backend when only other modules are mocked', () => {
    const config = { enable: false, include: ['^/api/v1/users', '^/api/v1/notifications'] };

    expect(isMockedUrl(config, '/api/v1/auth/session-login')).toBe(false);
    expect(isMockedUrl(config, '/hubs/realtime')).toBe(false);
    expect(isMockedUrl({ enable: false, include: '^/hubs/' }, '/hubs/realtime')).toBe(true);
  });

  it('installs no mocks and matches no request in the deployed build providers', () => {
    expect(deployed.provideMock(true)).toEqual([]);
    expect(deployed.mockInterceptors(true)).toEqual([]);
    expect(TestBed.inject(MOCKED_URL)('/api/v1/users')).toBe(false);
  });

  it('installs and matches mocks by the same rules in the development build providers', () => {
    expect(development.mockInterceptors(true).length).toBe(1);
    expect(development.mockInterceptors(false)).toEqual([]);
    expect(development.provideMock({ enable: false })).toEqual([]);

    TestBed.configureTestingModule({
      providers: development.provideMock({ enable: false, include: '^/api/v1/users' }),
    });
    const isMocked = TestBed.inject(MOCKED_URL);
    expect(isMocked('/api/v1/users')).toBe(true);
    expect(isMocked('/api/v1/roles')).toBe(false);
  });
});
