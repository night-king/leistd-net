import { HttpRequest, HttpResponse } from '@angular/common/http';
import { firstValueFrom, of } from 'rxjs';

import { mockInterceptors, provideMock } from './providers.mock';
import { USERS } from '../data/user';

/**
 * 用户数据与设置一样存本页 sessionStorage：请求处理完写回，Mock 下次启动（刷新、退出登录的整页跳转）恢复。
 * 否则整页跳转后用户回到初始数据而设置还在，例如开着"要求两步验证"却忘了已绑定的验证器。
 */
describe('Mock user persistence', () => {
  const initial = structuredClone(USERS);

  afterEach(() => USERS.splice(0, USERS.length, ...structuredClone(initial)));

  it('writes users back after a request and restores them on the next start', async () => {
    const [persist] = mockInterceptors(true);
    USERS[0].displayName = 'Changed by a request';
    await firstValueFrom(
      persist(new HttpRequest('POST', '/api/v1/anything', {}), () =>
        of(new HttpResponse({ status: 200 })),
      ),
    );

    // 整页跳转：页面内存回到初始数据
    USERS.splice(0, USERS.length, ...structuredClone(initial));
    expect(USERS[0].displayName).not.toBe('Changed by a request');

    provideMock(true);
    expect(USERS[0].displayName).toBe('Changed by a request');
  });

  it('keeps the initial users when nothing was stored yet', () => {
    provideMock(true);
    expect(USERS).toEqual(initial);
  });
});
