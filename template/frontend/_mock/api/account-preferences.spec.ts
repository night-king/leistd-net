import { HttpHeaders } from '@angular/common/http';

import { ACCOUNT_PREFERENCES_API, currentAccountPreferences } from './account-preferences';
import { MockException } from '../core/models';
import { TENANT_SETTING_VALUES, USER_SETTING_VALUES } from '../data/settings';
import { setMockSessionTenantKey, setMockSessionUserId } from '../utils/current-user';

type MockHandler = (req: { body: unknown; headers: HttpHeaders }) => unknown;

function request(body: unknown) {
  return { body, headers: new HttpHeaders() };
}

/** 模拟登录：真实环境下用户与租户在登录那一刻一起定案。 */
function signIn(userId: string): void {
  setMockSessionUserId(userId);
  setMockSessionTenantKey(null);
}

/** Mock 契约：与持有方同一回落，写入一次一个字段、成功返回完整偏好，失败码与后端一致。 */
describe('account preferences mock', () => {
  const patch = (ACCOUNT_PREFERENCES_API as unknown as Record<string, MockHandler>)[
    'PATCH /api/v1/auth/me/preferences'
  ];

  function failure(body: unknown): MockException {
    try {
      patch(request(body));
    } catch (error: unknown) {
      if (error instanceof MockException) return error;
      throw error;
    }
    throw new Error('expected the patch to fail');
  }

  beforeEach(() => {
    USER_SETTING_VALUES.clear();
    TENANT_SETTING_VALUES.clear();
    setMockSessionUserId(null);
    setMockSessionTenantKey(null);
  });

  afterEach(() => {
    setMockSessionUserId(null);
    setMockSessionTenantKey(null);
  });

  // 没有主体时与后端"读不到偏好"同形：两份都为 null
  it('reports the preferences as unavailable without a subject', () => {
    expect(currentAccountPreferences()).toEqual({ preferences: null, preferenceOverrides: null });
  });

  // 个人覆盖 → 组织默认 → 代码默认；外观没有组织层，默认跟随系统
  it('resolves the effective values from personal, organization and code defaults', () => {
    signIn('user_admin');
    TENANT_SETTING_VALUES.set('host:Display.TimeZone', 'Asia/Tokyo');

    expect(currentAccountPreferences()).toEqual({
      preferences: { language: null, timeZone: 'Asia/Tokyo', theme: 'system' },
      preferenceOverrides: { language: null, timeZone: null, theme: null },
    });
  });

  it('writes one field and returns the full preferences, clearing with null', () => {
    signIn('user_admin');
    TENANT_SETTING_VALUES.set('host:Display.TimeZone', 'Asia/Tokyo');

    const written = patch(request({ timeZone: 'Europe/London' }));
    expect(written).toEqual({
      preferences: { language: null, timeZone: 'Europe/London', theme: 'system' },
      preferenceOverrides: { language: null, timeZone: 'Europe/London', theme: null },
    });

    // 清除个人覆盖：生效值回落到组织默认
    const cleared = patch(request({ timeZone: null })) as {
      preferences: { timeZone: string | null };
    };
    expect(cleared.preferences.timeZone).toBe('Asia/Tokyo');
  });

  it('requires a subject', () => {
    expect(failure({ theme: 'dark' }).status).toBe(401);
  });

  // 形状不对是结构校验失败：400 字段错误、不带业务码，与后端一致
  for (const [label, body, field] of [
    ['two fields', { theme: 'dark', timeZone: 'UTC' }, 'body'],
    ['an empty object', {}, 'body'],
    ['an array body', ['theme'], 'body'],
    ['an unknown field', { fontSize: 'large' }, 'fontSize'],
    ['a number value', { theme: 1 }, 'theme'],
  ] as const) {
    it(`rejects ${label} as a malformed patch`, () => {
      signIn('user_admin');

      const error = failure(body);

      expect(error.status).toBe(400);
      expect(error.error).not.toHaveProperty('code');
      expect((error.error as { errors: { field: string }[] }).errors).toEqual([
        expect.objectContaining({ field }),
      ]);
    });
  }

  // 空串与其他不认识的值一样：不在允许列表
  for (const value of ['sepia', '']) {
    it(`rejects the appearance '${value}' with the allowed values`, () => {
      signIn('user_admin');

      const error = failure({ theme: value });

      expect(error.status).toBe(400);
      expect(error.error).toMatchObject({
        code: 'Setting:ValueNotAllowed',
        data: { Value: value, Allowed: ['system', 'light', 'dark'] },
      });
    });
  }

  // Windows 时区 ID 后端认、浏览器不认，两端统一只收 IANA
  for (const value of ['China Standard Time', '']) {
    it(`rejects the time zone '${value}'`, () => {
      signIn('user_admin');

      const error = failure({ timeZone: value });

      expect(error.status).toBe(400);
      expect(error.error).toMatchObject({
        code: 'AppSetting:TimeZoneInvalid',
        data: { Value: value },
      });
    });
  }

  // 个人覆盖按主体键控：切换用户后看不到上一个人的偏好
  it('keeps each user’s overrides separate', () => {
    signIn('user_admin');
    patch(request({ theme: 'dark' }));

    signIn('user_demo');

    expect(currentAccountPreferences().preferenceOverrides?.theme).toBeNull();
  });
});
