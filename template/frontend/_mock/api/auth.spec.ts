//#if (LocalIdentity)
import { HttpHeaders } from '@angular/common/http';

import { AUTH_API } from './auth';
//#if (IncludeMultiTenancy)
import { TENANT_HEADER } from '../../src/app/core/tenancy/tenant-protocol';
//#endif
import { MockException } from '../core/models';
import { TENANT_SETTING_VALUES } from '../data/settings';
import { DELETED_USERS, MockUser, USERS } from '../data/user';
import {
  ensureMockReauthentication,
  expireMockSessionAuthentication,
  //#if (Impersonation)
  getMockImpersonator,
  //#endif
  getMockSessionTenantKey,
  getMockSessionUserId,
  //#if (Impersonation)
  setMockImpersonator,
  //#endif
  //#if (Impersonation || IncludeMultiTenancy)
  setMockSessionTenantKey,
  //#endif
  setMockSessionUserId,
} from '../utils/current-user';

type MockHandler = (req: { body: unknown }) => unknown;

/** Mock 的受保护写端点与真后端同口径：无会话即 401，不回落到默认用户。 */
describe('mock auth subject', () => {
  const profile = AUTH_API['PUT /api/v1/auth/me'] as MockHandler;
  const changePassword = AUTH_API['POST /api/v1/auth/change-password'] as MockHandler;

  beforeEach(() => setMockSessionUserId(null));

  function expect401(run: () => unknown): void {
    const error = (() => {
      try {
        run();
        return null;
      } catch (e) {
        return e;
      }
    })();

    expect(error).toBeInstanceOf(MockException);
    expect((error as MockException).status).toBe(401);
  }

  it('returns 401 for an anonymous profile update and changes no user', () => {
    const before = USERS.map((u) => ({ ...u }));

    expect401(() => profile({ body: { username: 'hacked', email: 'hacked@test.dev' } }));

    expect(USERS.map((u) => ({ ...u }))).toEqual(before);
  });

  it('keeps the session in sessionStorage and reads it on every request', () => {
    // 会话写进当前页面的 sessionStorage
    setMockSessionUserId(USERS[0].id);
    expect(sessionStorage.getItem('mock_session_user_id')).toBe(USERS[0].id);

    // 存储是唯一来源：存储里没有会话，模块不能凭记下的旧标识继续放行
    sessionStorage.removeItem('mock_session_user_id');

    expect401(() => profile({ body: { username: 'hacked', email: 'hacked@test.dev' } }));
  });

  it('returns 401 for an anonymous password change and keeps the old password valid', () => {
    const passwords = USERS.map((u) => u.password);

    expect401(() =>
      changePassword({ body: { currentPassword: 'whatever', newPassword: 'Passw0rd!x' } }),
    );

    expect(USERS.map((u) => u.password)).toEqual(passwords);
  });

  it('also returns 401 when the session user ID matches no mock user', () => {
    setMockSessionUserId('00000000-0000-0000-0000-000000000000');

    expect401(() => profile({ body: { username: 'x', email: 'x@test.dev' } }));
  });
});

interface MockRequestLike {
  body?: unknown;
  params?: Record<string, string>;
  headers?: HttpHeaders;
}
type Handler = (req: MockRequestLike) => unknown;

function rejectionOf(run: () => unknown): MockException {
  try {
    run();
  } catch (error) {
    expect(error).toBeInstanceOf(MockException);
    return error as MockException;
  }
  throw new Error('expected the handler to reject');
}

/**
 * 注册的占用判定与后端同一口径：按唯一索引原样比较（只差大小写的是另一个地址，原样保存），
 * 已删除用户仍占着用户名和邮箱。
 */
describe('mock registration occupancy', () => {
  const register = AUTH_API['POST /api/v1/auth/register'] as Handler;
  const captcha = AUTH_API['GET /api/v1/auth/captcha'] as () => {
    captchaToken: string;
    captchaImageBase64: string;
  };
  let users: MockUser[];

  beforeEach(() => {
    users = [...USERS];
  });

  afterEach(() => {
    USERS.length = 0;
    USERS.push(...users);
    DELETED_USERS.length = 0;
  });

  /** 从验证码图片里读出文字，模拟用户照图输入。 */
  function solvedCaptcha(): { captchaToken: string; captchaCode: string } {
    const { captchaToken, captchaImageBase64 } = captcha();
    const svg = atob(captchaImageBase64.split(',')[1]);
    return { captchaToken, captchaCode: /<text[^>]*>([^<]+)<\/text>/.exec(svg)![1] };
  }

  function registration(username: string, email: string) {
    return { username, email, password: 'Spec@1234567890', ...solvedCaptcha() };
  }

  // 租户关闭了自助注册：注册被拒（带码的 403），与后端一致
  it('rejects registration when self sign-up is closed', () => {
    TENANT_SETTING_VALUES.set('host:Registration.Enabled', 'false');
    try {
      const error = rejectionOf(() =>
        register({
          headers: new HttpHeaders(),
          body: registration('spec-closed', 'closed@example.com'),
        }),
      );
      expect(error.status).toBe(403);
      expect(error.error.code).toBe('Auth:RegistrationDisabled');
    } finally {
      TENANT_SETTING_VALUES.delete('host:Registration.Enabled');
    }
  });

  it('registers an address that differs from a taken one only by case and keeps its case', () => {
    register({ headers: new HttpHeaders(), body: registration('spec-case', 'DEMO@Example.com') });

    expect(USERS.find((u) => u.username === 'spec-case')?.email).toBe('DEMO@Example.com');
  });

  it('rejects the username and email of a deleted user with 409', () => {
    DELETED_USERS.push({
      ...USERS.find((u) => u.username === 'demo')!,
      id: 'user_deleted',
      username: 'gone',
      email: 'gone@example.com',
    });

    const username = rejectionOf(() =>
      register({ headers: new HttpHeaders(), body: registration('gone', 'fresh@example.com') }),
    );
    expect(username.status).toBe(409);
    expect(username.error.code).toBe('User:UsernameTaken');

    const email = rejectionOf(() =>
      register({ headers: new HttpHeaders(), body: registration('fresh', 'gone@example.com') }),
    );
    expect(email.status).toBe(409);
    expect(email.error.code).toBe('User:EmailTaken');
    expect(USERS).toEqual(users);
  });
});

/** 本人头像只收图片 data URL：外部地址等是 400 字段错误、不带业务码，头像不变。 */
describe('mock self avatar', () => {
  const setAvatar = AUTH_API['PUT /api/v1/auth/me/avatar'] as Handler;
  const admin = USERS.find((u) => u.username === 'admin')!;
  let original: string | undefined;

  beforeEach(() => {
    original = admin.avatar;
    setMockSessionUserId(admin.id);
  });

  afterEach(() => {
    admin.avatar = original;
    setMockSessionUserId(null);
  });

  it.each([
    ['an external URL', 'https://example.com/avatar.png'],
    ['a non-image data URL', 'data:text/plain;base64,aGVsbG8='],
    ['an unsupported image type', 'data:image/svg+xml;base64,PHN2Zy8+'],
  ])('rejects %s as a field error without a code', (_, avatar) => {
    const error = rejectionOf(() => setAvatar({ body: { avatar } }));

    expect(error.status).toBe(400);
    expect(error.error.code).toBeUndefined();
    expect(error.error.errors.map((item: { field: string }) => item.field)).toEqual(['avatar']);
    expect(admin.avatar).toBe(original);
  });

  it('accepts an image data URL and clears with an empty value', () => {
    setAvatar({ body: { avatar: 'data:image/png;base64,iVBORw0KGgo=' } });
    expect(admin.avatar).toBe('data:image/png;base64,iVBORw0KGgo=');

    setAvatar({ body: { avatar: '' } });
    expect(admin.avatar).toBeUndefined();
  });
});

/**
 * 登录第二步：启用了两步验证的账号，第一步只拿到凭据、不下发会话；第二步的拒绝形态与后端一致——
 * 入参缺失 400 字段错误，凭据无效或用尽 401，验证码错误 400。
 */
describe('mock two-factor sign-in', () => {
  const sessionLogin = AUTH_API['POST /api/v1/auth/session-login'] as Handler;
  const completeTwoFactor = AUTH_API['POST /api/v1/auth/two-factor'] as Handler;
  const demo = USERS.find((u) => u.username === 'demo')!;
  const headers = new HttpHeaders();

  beforeEach(() => {
    setMockSessionUserId(null);
    demo.twoFactorEnabled = true;
    demo.recoveryCodes = ['mock-recovery-1'];
  });

  afterEach(() => {
    setMockSessionUserId(null);
    demo.twoFactorEnabled = false;
    demo.recoveryCodes = [];
  });

  function firstStep(): string {
    const result = sessionLogin({
      headers,
      body: { usernameOrEmail: 'demo', password: demo.password },
    }) as { requiresTwoFactor?: boolean; twoFactorToken?: string };
    expect(result.requiresTwoFactor).toBe(true);
    expect(getMockSessionUserId()).toBeNull();
    return result.twoFactorToken!;
  }

  it('answers the password step with a challenge instead of a session', () => {
    expect(firstStep()).toBeTruthy();
  });

  it('rejects a missing token and code with 400 field errors and no business code', () => {
    const error = rejectionOf(() => completeTwoFactor({ headers, body: {} }));

    expect(error.status).toBe(400);
    expect(error.error.code).toBeUndefined();
    expect(error.error.errors.map((item: { field: string }) => item.field)).toEqual([
      'token',
      'code',
    ]);
    expect(getMockSessionUserId()).toBeNull();
  });

  it('rejects an unknown challenge with 401', () => {
    const error = rejectionOf(() =>
      completeTwoFactor({ headers, body: { token: 'unknown', code: '123456' } }),
    );

    expect(error.status).toBe(401);
    expect(error.error.code).toBe('Auth:TwoFactorChallengeExpired');
  });

  it('rejects a wrong code with 400 and voids the challenge once the attempts run out', () => {
    const token = firstStep();

    for (let attempt = 1; attempt < 5; attempt++) {
      const error = rejectionOf(() =>
        completeTwoFactor({ headers, body: { token, code: '000000' } }),
      );
      expect(error.status).toBe(400);
      expect(error.error.code).toBe('Auth:TwoFactorCodeInvalid');
    }

    expect(
      rejectionOf(() => completeTwoFactor({ headers, body: { token, code: '000000' } })).status,
    ).toBe(401);
    // 用尽之后正确的码也不再被接受：只能回到第一步
    expect(
      rejectionOf(() => completeTwoFactor({ headers, body: { token, code: '123456' } })).status,
    ).toBe(401);
    expect(getMockSessionUserId()).toBeNull();
  });

  it('signs in with the code and accepts each challenge only once', () => {
    const token = firstStep();

    completeTwoFactor({ headers, body: { token, code: '123 456' } });

    expect(getMockSessionUserId()).toBe(demo.id);
    expect(getMockSessionTenantKey()).toBe('host');
    expect(
      rejectionOf(() => completeTwoFactor({ headers, body: { token, code: '123456' } })).status,
    ).toBe(401);
  });

  it('consumes a recovery code so it cannot be used twice', () => {
    completeTwoFactor({ headers, body: { token: firstStep(), recoveryCode: 'mock-recovery-1' } });

    expect(getMockSessionUserId()).toBe(demo.id);
    expect(demo.recoveryCodes).toEqual([]);

    setMockSessionUserId(null);
    const error = rejectionOf(() =>
      completeTwoFactor({ headers, body: { token: firstStep(), recoveryCode: 'mock-recovery-1' } }),
    );
    expect(error.status).toBe(400);
    expect(getMockSessionUserId()).toBeNull();
  });
});

/**
 * 确认身份与后端同一组可观察规则：刚认证过放行；出示了凭证只按它判定（方式不具备、口令或验证码不对各有错误码，
 * 两种同时出示是字段错误）；会话不够新时既无口令又没开两步验证要求重新登录，其余要求确认身份。
 */
describe('mock reauthentication', () => {
  const profile = AUTH_API['PUT /api/v1/auth/me'] as Handler;
  const beginSetup = AUTH_API['POST /api/v1/auth/me/two-factor/setup'] as Handler;
  const disable = AUTH_API['POST /api/v1/auth/me/two-factor/disable'] as Handler;
  const demo = USERS.find((u) => u.username === 'demo')!;
  const admin = USERS.find((u) => u.username === 'admin')!;
  const snapshot = { ...demo };

  beforeEach(() => {
    setMockSessionUserId(demo.id);
    demo.twoFactorEnabled = true;
    demo.recoveryCodes = ['mock-recovery-1'];
  });

  afterEach(() => {
    setMockSessionUserId(null);
    Object.assign(demo, snapshot, { recoveryCodes: [...(snapshot.recoveryCodes ?? [])] });
  });

  function emailChange(extra: Record<string, unknown> = {}) {
    return { body: { username: demo.username, email: 'demo@new.example.com', ...extra } };
  }

  it('lets a recent session change the email without a proof', () => {
    profile(emailChange());

    expect(demo.email).toBe('demo@new.example.com');
  });

  it('asks a stale session to confirm and judges only the proof that was shown', () => {
    expireMockSessionAuthentication();

    expect(rejectionOf(() => profile(emailChange())).error.code).toBe(
      'Auth:ReauthenticationRequired',
    );
    expect(rejectionOf(() => profile(emailChange({ currentPassword: 'wrong' }))).error.code).toBe(
      'Security:CurrentPasswordIncorrect',
    );
    expect(rejectionOf(() => profile(emailChange({ twoFactorCode: '000000' }))).error.code).toBe(
      'Auth:TwoFactorCodeInvalid',
    );
    expect(demo.email).toBe(snapshot.email);

    profile(emailChange({ twoFactorCode: '123456' }));
    expect(demo.email).toBe('demo@new.example.com');
  });

  it('rejects both proofs at once as a field error without a business code', () => {
    const error = rejectionOf(() =>
      profile(emailChange({ currentPassword: demo.password, twoFactorCode: '123456' })),
    );

    expect(error.status).toBe(400);
    expect(error.error.code).toBeUndefined();
    expect(error.error.errors[0].field).toBe('twoFactorCode');
  });

  // 出示了本账号不具备的方式：不回落到"刚认证过"，按方式不具备拒绝
  it('rejects a method the account does not have even on a recent session', () => {
    demo.twoFactorEnabled = false;

    expect(rejectionOf(() => profile(emailChange({ twoFactorCode: '123456' }))).error.code).toBe(
      'Auth:ReauthenticationMethodUnavailable',
    );
  });

  it('asks an account without a password or two-factor to sign in again', () => {
    expireMockSessionAuthentication();
    const passwordless = { ...admin, password: undefined, twoFactorEnabled: false } as MockUser;

    expect(rejectionOf(() => ensureMockReauthentication(passwordless, {})).error.code).toBe(
      'Auth:SignInAgainRequired',
    );
    expect(
      rejectionOf(() => ensureMockReauthentication(passwordless, { password: 'x' })).error.code,
    ).toBe('Auth:ReauthenticationMethodUnavailable');
  });

  // 换绑：现有验证器上的码同时算确认身份；恢复码不算，会话不够新时先确认身份，确认不过不用掉恢复码
  it('lets the current code replace on a stale session but not a recovery code alone', () => {
    expireMockSessionAuthentication();

    expect(beginSetup({ body: { code: '123456' } })).toHaveProperty('setupId');
    expect(
      rejectionOf(() => beginSetup({ body: { recoveryCode: 'mock-recovery-1' } })).error.code,
    ).toBe('Auth:ReauthenticationRequired');
    expect(demo.recoveryCodes).toEqual(['mock-recovery-1']);

    beginSetup({ body: { recoveryCode: 'mock-recovery-1', password: demo.password } });
    expect(demo.recoveryCodes).toEqual([]);
  });

  it('requires the password to turn off two-factor when the account has one', () => {
    expect(rejectionOf(() => disable({ body: { code: '123456' } })).error.code).toBe(
      'Security:PasswordRequired',
    );

    disable({ body: { password: demo.password, code: '123456' } });
    expect(demo.twoFactorEnabled).toBe(false);
  });
});
//#if (ExternalLogin)

describe('mock external link completion', () => {
  const complete = AUTH_API['POST /api/v1/external-auth/:provider/link/complete'] as Handler;
  const links = AUTH_API['GET /api/v1/external-auth/links'] as () => {
    providers: { provider: string; link: { id: string } | null }[];
  };
  const unlink = AUTH_API['DELETE /api/v1/external-auth/links/:id'] as Handler;

  afterEach(() => setMockSessionUserId(null));

  it('requires a session', () => {
    setMockSessionUserId(null);

    expect(rejectionOf(() => complete({ params: { provider: 'google' } })).status).toBe(401);
  });

  it('rejects an unconfigured provider as an invalid intent and a second link with 409', () => {
    setMockSessionUserId('user_admin');

    const unknown = rejectionOf(() => complete({ params: { provider: 'gitlab' } }));
    expect(unknown.status).toBe(400);
    expect(unknown.error.code).toBe('ExternalAuth:InvalidState');

    const duplicate = rejectionOf(() => complete({ params: { provider: 'github' } }));
    expect(duplicate.status).toBe(409);
    expect(duplicate.error.code).toBe('ExternalAuth:ProviderAlreadyLinked');
  });

  it('links the provider so the bindings list shows it', () => {
    setMockSessionUserId('user_admin');

    complete({ params: { provider: 'google' } });

    const google = links().providers.find((item) => item.provider === 'google');
    expect(google?.link).not.toBeNull();
    unlink({ params: { id: google!.link!.id } });
  });

  it('asks a stale session to confirm before linking and links with the shown password', () => {
    const admin = USERS.find((u) => u.id === 'user_admin')!;
    setMockSessionUserId(admin.id);
    expireMockSessionAuthentication();

    expect(rejectionOf(() => complete({ params: { provider: 'google' } })).error.code).toBe(
      'Auth:ReauthenticationRequired',
    );
    complete({ params: { provider: 'google' }, body: { password: admin.password } });

    const google = links().providers.find((item) => item.provider === 'google');
    expect(google?.link).not.toBeNull();
    // 解绑同样先确认身份，确认之前绑定不变
    expect(rejectionOf(() => unlink({ params: { id: google!.link!.id } })).error.code).toBe(
      'Auth:ReauthenticationRequired',
    );
    unlink({ params: { id: google!.link!.id }, body: { password: admin.password } });
    expect(links().providers.find((item) => item.provider === 'google')?.link).toBeNull();
  });
});

/** 只经外部登录建立的账号（octo）：没有口令、已开两步验证。 */
describe('mock passwordless account', () => {
  const setPassword = AUTH_API['POST /api/v1/auth/me/password'] as Handler;
  const unlink = AUTH_API['DELETE /api/v1/external-auth/links/:id'] as Handler;
  const disable = AUTH_API['POST /api/v1/auth/me/two-factor/disable'] as Handler;
  const callback = AUTH_API['POST /api/v1/external-auth/:provider/complete'] as Handler;
  const me = AUTH_API['GET /api/v1/auth/me'] as Handler;
  const octo = USERS.find((u) => u.id === 'user_octo')!;
  const password = 'First!Passw0rd';

  beforeEach(() => setMockSessionUserId(octo.id));

  afterEach(() => {
    setMockSessionUserId(null);
    octo.password = undefined;
    octo.twoFactorEnabled = true;
  });

  it('signs in from Google into the second step and reports no password', () => {
    setMockSessionUserId(null);

    const result = callback({ params: { provider: 'google' }, headers: new HttpHeaders() }) as {
      requiresTwoFactor?: boolean;
    };
    expect(result.requiresTwoFactor).toBe(true);
    expect(getMockSessionUserId()).toBeNull();

    setMockSessionUserId(octo.id);
    expect(me({})).toMatchObject({ hasPassword: false, isTwoFactorEnabled: true });
  });

  // 刚完成外部登录的会话可以直接设置；会话不够新时只能出示验证码
  it('sets a first password on a recent session and asks a stale one for the code', () => {
    expireMockSessionAuthentication();
    const body = { newPassword: password, confirmPassword: password };

    expect(rejectionOf(() => setPassword({ body })).error.code).toBe(
      'Auth:ReauthenticationRequired',
    );
    setPassword({ body: { ...body, twoFactorCode: '123456' } });
    expect(octo.password).toBe(password);

    const again = rejectionOf(() => setPassword({ body }));
    expect(again.status).toBe(409);
    expect(again.error.code).toBe('Security:PasswordAlreadySet');
  });

  it('keeps the only sign-in method and confirms identity before unlinking', () => {
    const only = rejectionOf(() => unlink({ params: { id: 'mock-link-github' } }));
    expect(only.status).toBe(409);
    expect(only.error.code).toBe('ExternalAuth:LastSignInMethod');

    // 不存在或不属于本人的绑定幂等成功
    expect(unlink({ params: { id: 'missing' } })).toBe('ok');
  });

  it('turns off two-factor with the code alone and rejects a password it does not have', () => {
    expect(rejectionOf(() => disable({ body: { password: 'x', code: '123456' } })).error.code).toBe(
      'Auth:ReauthenticationMethodUnavailable',
    );

    disable({ body: { code: '123456' } });
    expect(octo.twoFactorEnabled).toBe(false);
  });
});
//#endif
//#if (Impersonation)

describe('mock impersonation session', () => {
  const status = AUTH_API['GET /api/v1/auth/impersonation'] as () => {
    isImpersonating: boolean;
    impersonatorName?: string;
    tenantName?: string;
  };
  const end = AUTH_API['POST /api/v1/auth/end-impersonation'] as () => unknown;

  afterEach(() => setMockSessionUserId(null));

  it('requires a session for both the status and the exit', () => {
    setMockSessionUserId(null);

    expect(rejectionOf(() => status()).status).toBe(401);
    expect(rejectionOf(() => end()).status).toBe(401);
  });

  it('reports no impersonation and refuses to end one with 409', () => {
    setMockSessionUserId('user_admin');

    expect(status()).toEqual({ isImpersonating: false });
    const error = rejectionOf(() => end());
    expect(error.status).toBe(409);
    expect(error.error.code).toBe('Tenant:NotImpersonating');
  });

  it('names the impersonator and tenant, then returns the session to the impersonator', () => {
    setMockSessionUserId('user_demo');
    setMockSessionTenantKey('tenant_acme');
    setMockImpersonator({ userId: 'user_admin', name: 'Administrator', tenantKey: 'host' });

    expect(status()).toEqual({
      isImpersonating: true,
      impersonatorName: 'Administrator',
      tenantName: 'Acme Corp',
    });

    end();

    expect(getMockSessionUserId()).toBe('user_admin');
    expect(getMockSessionTenantKey()).toBe('host');
    expect(getMockImpersonator()).toBeNull();
    expect(status()).toEqual({ isImpersonating: false });
  });
});
//#endif
//#if (IncludeMultiTenancy)

/** 注册开关按租户：匿名看租户提示头，已登录以会话的租户为准（提示头改写不了），与后端租户解析一致。 */
describe('mock registration switch tenancy', () => {
  const summary = AUTH_API['GET /api/v1/auth/security-config'] as unknown as (req: {
    headers: HttpHeaders;
  }) => { registrationEnabled: boolean };
  const open = (headers: Record<string, string> = {}) =>
    summary({ headers: new HttpHeaders(headers) }).registrationEnabled;

  afterEach(() => {
    TENANT_SETTING_VALUES.clear();
    setMockSessionUserId(null);
  });

  it('follows the tenant hint for anonymous visitors', () => {
    TENANT_SETTING_VALUES.set('tenant_acme:Registration.Enabled', 'false');

    expect(open({ [TENANT_HEADER]: 'tenant_acme' })).toBe(false);
    expect(open()).toBe(true);
  });

  it('follows the session tenant once signed in, whatever the hint says', () => {
    TENANT_SETTING_VALUES.set('host:Registration.Enabled', 'false');
    setMockSessionUserId(USERS[0].id);
    setMockSessionTenantKey('tenant_acme');

    expect(open()).toBe(true);
    TENANT_SETTING_VALUES.set('tenant_acme:Registration.Enabled', 'false');
    expect(open({ [TENANT_HEADER]: 'tenant_globex' })).toBe(false);

    setMockSessionTenantKey(null);
    TENANT_SETTING_VALUES.delete('host:Registration.Enabled');
    expect(open({ [TENANT_HEADER]: 'tenant_acme' })).toBe(true);
  });
});
//#endif
//#endif
