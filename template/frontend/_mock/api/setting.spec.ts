import { HttpHeaders } from '@angular/common/http';

//#if (LocalIdentity)
import { AUTH_API } from './auth';
//#endif
import { SETTING_API } from './setting';
//#if (LocalIdentity)
import { TENANT_HEADER } from '../../src/app/core/tenancy/tenant-protocol';
//#endif
import { MockException } from '../core/models';
import { TENANT_SETTING_VALUES, USER_SETTING_VALUES } from '../data/settings';
//#if (ExternalLogin && IncludeMultiTenancy)
import { USERS } from '../data/user';
//#endif
import { setMockSessionTenantKey, setMockSessionUserId } from '../utils/current-user';

type MockHandler = (req: { body: unknown; headers: HttpHeaders }) => unknown;

function request(body: unknown = null, headers: Record<string, string> = {}) {
  return { body, headers: new HttpHeaders(headers) };
}

/** 模拟登录：真实环境下用户与租户在登录那一刻一起定案。 */
function signIn(userId: string, tenantKey: string | null = null): void {
  setMockSessionUserId(userId);
  setMockSessionTenantKey(tenantKey);
}

interface SettingRow {
  name: string;
  displayName: string;
  group: string;
  groupDisplayName: string;
  userValue: string | null;
  tenantValue: string | null;
}

/** Mock 契约：除路由外还断言身份、权限、错误状态与主体隔离，与真实 API 的差异会在联调时才暴露。 */
describe('settings mock', () => {
  const api = SETTING_API as unknown as Record<string, MockHandler>;
  const get = 'GET /api/v1/settings';
  const putUser = 'PUT /api/v1/settings/current-user';
  const putTenant = 'PUT /api/v1/settings/current-tenant';
  function statusOf(run: () => unknown): number {
    try {
      run();
    } catch (error: unknown) {
      if (error instanceof MockException) return error.status;
      throw error;
    }
    return 200;
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

  it('registers the settings endpoints', () => {
    //#if (Email)
    // 测试邮件端点仅随有效邮件能力生成
    expect(Object.keys(SETTING_API).sort()).toEqual([
      get,
      'POST /api/v1/settings/email/test',
      putTenant,
      putUser,
    ]);
    //#else
    expect(Object.keys(SETTING_API).sort()).toEqual([get, putTenant, putUser]);
    //#endif
  });

  // Controller 整体 [Authorize]：匿名一律 401，不是空列表。
  it('requires authentication', () => {
    expect(statusOf(() => api[get](request()))).toBe(401);
    expect(statusOf(() => api[putUser](request({ name: 'Display.TimeZone', value: 'UTC' })))).toBe(
      401,
    );
  });

  // 改自己的偏好只要登录，改系统默认值要 App.Settings——两个端点存在的理由就是这层差异。
  it('separates personal preferences from system defaults by permission', () => {
    signIn('user_demo');
    expect(statusOf(() => api[putUser](request({ name: 'Display.TimeZone', value: 'UTC' })))).toBe(
      200,
    );
    expect(
      statusOf(() => api[putTenant](request({ name: 'Display.TimeZone', value: 'UTC' }))),
    ).toBe(403);

    signIn('user_admin');
    expect(
      statusOf(() => api[putTenant](request({ name: 'Display.TimeZone', value: 'UTC' }))),
    ).toBe(200);
  });

  // 用户级覆盖按主体键控：共用一份全局值会让切换用户后看到上一个人的偏好。
  it('keeps user overrides separate per user', () => {
    signIn('user_admin');
    api[putUser](request({ name: 'Display.TimeZone', value: 'UTC' }));

    signIn('user_demo');
    const demoRows = api[get](request()) as SettingRow[];
    expect(demoRows.find((s) => s.name === 'Display.TimeZone')?.userValue).toBeNull();

    signIn('user_admin');
    const adminRows = api[get](request()) as SettingRow[];
    expect(adminRows.find((s) => s.name === 'Display.TimeZone')?.userValue).toBe('UTC');
  });

  it('applies layered writes and null clears an override', () => {
    signIn('user_admin');
    api[putUser](request({ name: 'Display.TimeZone', value: 'UTC' }));
    api[putTenant](request({ name: 'Display.TimeZone', value: 'America/New_York' }));

    let row = (api[get](request()) as SettingRow[]).find((s) => s.name === 'Display.TimeZone');
    expect(row?.userValue).toBe('UTC');
    expect(row?.tenantValue).toBe('America/New_York');

    api[putUser](request({ name: 'Display.TimeZone', value: null }));
    row = (api[get](request()) as SettingRow[]).find((s) => s.name === 'Display.TimeZone');
    expect(row?.userValue).toBeNull();
    expect(row?.tenantValue).toBe('America/New_York');
  });

  //#if (IncludeMultiTenancy)
  // 用户级覆盖按 `{tenant}:u:{userId}` 隔离；租户随登录一起定案，之后请求头改不动它。
  it('keeps overrides separate per tenant', () => {
    const acme = 'tenant_acme';
    const globex = 'tenant_globex';

    signIn('user_admin', acme);
    api[putUser](request({ name: 'Display.TimeZone', value: 'UTC' }));
    api[putTenant](request({ name: 'Display.TimeZone', value: 'Europe/London' }));

    signIn('user_admin', globex);
    const inGlobex = (api[get](request()) as SettingRow[]).find(
      (s) => s.name === 'Display.TimeZone',
    );
    expect(inGlobex?.userValue).toBeNull();
    expect(inGlobex?.tenantValue).toBeNull();

    signIn('user_admin', acme);
    const backInAcme = (api[get](request()) as SettingRow[]).find(
      (s) => s.name === 'Display.TimeZone',
    );
    expect(backInAcme?.userValue).toBe('UTC');
    expect(backInAcme?.tenantValue).toBe('Europe/London');

    // 宿主上下文（登录时没有租户）同样与任何租户隔离
    signIn('user_admin');
    const inHost = (api[get](request()) as SettingRow[]).find((s) => s.name === 'Display.TimeZone');
    expect(inHost?.userValue).toBeNull();
    expect(inHost?.tenantValue).toBeNull();
  });

  //#if (LocalIdentity)
  // 从登录入口进：租户由请求头定案写进会话，设置读写按会话走；上面直接摆会话的用例测不到这段接线。
  it('picks up the tenant pinned by the login endpoint', () => {
    const auth = AUTH_API as unknown as Record<
      string,
      (req: { body: unknown; headers: HttpHeaders }) => unknown
    >;
    const acme = 'tenant_acme';

    auth['POST /api/v1/auth/session-login']({
      body: { usernameOrEmail: 'admin', password: 'Admin@123456' },
      headers: new HttpHeaders({ [TENANT_HEADER]: acme }),
    });

    api[putUser](request({ name: 'Display.TimeZone', value: 'UTC' }));

    // 写入落在登录时定案的那个租户下，而不是宿主
    expect(USER_SETTING_VALUES.get(`${acme}:user_admin:Display.TimeZone`)).toBe('UTC');
    expect(USER_SETTING_VALUES.get('host:user_admin:Display.TimeZone')).toBeUndefined();
  });
  //#if (ExternalLogin)

  // 外部登录是另一条入口：回调仍是匿名请求、会带上登录前选定的租户头，真实后端在这一步
  // 进入该租户上下文。回调把租户固定成宿主的话，密码登录那条用例照样全绿。
  it('picks up the tenant pinned by the external login callback', () => {
    const auth = AUTH_API as unknown as Record<
      string,
      (req: { body: unknown; headers: HttpHeaders; params?: Record<string, string> }) => unknown
    >;
    const acme = 'tenant_acme';

    auth['POST /api/v1/external-auth/:provider/complete']({
      body: {},
      headers: new HttpHeaders({ [TENANT_HEADER]: acme }),
      params: { provider: 'github' },
    });

    api[putUser](request({ name: 'Display.TimeZone', value: 'Europe/London' }));

    const subject = USERS[0].id;
    expect(USER_SETTING_VALUES.get(`${acme}:${subject}:Display.TimeZone`)).toBe('Europe/London');
    expect(USER_SETTING_VALUES.get(`host:${subject}:Display.TimeZone`)).toBeUndefined();
  });
  //#endif

  //#endif
  //#else
  it('rejects a non-host mock session before changing settings', () => {
    signIn('user_admin');
    expect(statusOf(() => setMockSessionTenantKey('tenant_acme'))).toBe(401);
    api[putUser](request({ name: 'Display.TimeZone', value: 'UTC' }));
    expect(USER_SETTING_VALUES.get('host:user_admin:Display.TimeZone')).toBe('UTC');
    expect(USER_SETTING_VALUES.get('tenant_acme:user_admin:Display.TimeZone')).toBeUndefined();
  });

  //#if (LocalIdentity)
  it('ignores anonymous tenant hints when establishing a host session', () => {
    const auth = AUTH_API as unknown as Record<string, MockHandler>;
    auth['POST /api/v1/auth/session-login'](
      request(
        { usernameOrEmail: 'admin', password: 'Admin@123456' },
        { [TENANT_HEADER]: 'tenant_acme' },
      ),
    );
    api[putUser](request({ name: 'Display.TimeZone', value: 'UTC' }));
    expect(USER_SETTING_VALUES.get('host:user_admin:Display.TimeZone')).toBe('UTC');
    expect(USER_SETTING_VALUES.get('tenant_acme:user_admin:Display.TimeZone')).toBeUndefined();
  });

  it('rejects a stored tenant session before returning the local user or changing settings', () => {
    signIn('user_admin');
    sessionStorage.setItem('mock_session_tenant_key', 'tenant_acme');
    const auth = AUTH_API as unknown as Record<string, MockHandler>;
    expect(statusOf(() => auth['GET /api/v1/auth/me'](request()))).toBe(401);
    expect(statusOf(() => api[putUser](request({ name: 'Display.TimeZone', value: 'UTC' })))).toBe(
      401,
    );
    expect(USER_SETTING_VALUES.size).toBe(0);
  });
  //#endif
  //#endif
  it('writes personal settings for the account the stored session names', () => {
    signIn('user_admin');
    // 会话只在存储里换成了 demo：设置按存储里的会话记，不按模块记下的旧主体
    sessionStorage.setItem('mock_session_user_id', 'user_demo');

    api[putUser](request({ name: 'Display.TimeZone', value: 'UTC' }));

    expect(USER_SETTING_VALUES.get('host:user_demo:Display.TimeZone')).toBe('UTC');
    expect(USER_SETTING_VALUES.has('host:user_admin:Display.TimeZone')).toBe(false);
  });

  it('rejects the values and names the backend rejects', () => {
    signIn('user_admin');

    // 未定义的设置名 → 404；先查定义再校验值，顺序与后端一致
    expect(statusOf(() => api[putUser](request({ name: 'Nope.Missing', value: 'x' })))).toBe(404);
    // 空串不是清除协议——清除只有 null
    expect(statusOf(() => api[putUser](request({ name: 'Display.TimeZone', value: '' })))).toBe(
      400,
    );
    // Windows 时区 ID：后端认、浏览器不认，两端必须统一只收 IANA
    expect(
      statusOf(() =>
        api[putUser](request({ name: 'Display.TimeZone', value: 'China Standard Time' })),
      ),
    ).toBe(400);
  });

  // 显示名按后端 `Setting:{name}` / `SettingGroup:{group}` 词条返回，不回显分组标识。
  function minimumLevel(headers: Record<string, string> = {}): SettingRow {
    signIn('user_admin');
    const row = (api[get](request(null, headers)) as SettingRow[]).find(
      (s) => s.name === 'Logging.MinimumLevel',
    );
    if (!row) throw new Error('Logging.MinimumLevel is not in the list');
    return row;
  }

  it('returns English display names from the backend resources', () => {
    const row = minimumLevel({ 'Accept-Language': 'en' });

    expect(row.displayName).toBe('Minimum log level');
    expect(row.groupDisplayName).toBe('Operations');
  });
  //#if (IncludeLocalization)

  it('follows the request language for setting and group names', () => {
    const row = minimumLevel({ 'Accept-Language': 'zh-CN' });

    expect(row.displayName).toBe('最小日志级别');
    expect(row.groupDisplayName).toBe('运维');
    // 分组标识本身不随语言变：前端按它分面板、进 URL。
    expect(row.group).toBe('Operations');
  });

  it('falls back to English when the request language is not supported', () => {
    expect(minimumLevel({ 'Accept-Language': 'ja' }).groupDisplayName).toBe('Operations');
    expect(minimumLevel().groupDisplayName).toBe('Operations');
  });
  //#else

  it('keeps English names when localization is not included', () => {
    // 未启用本地化的项目没有中文词条，请求头说什么都返回英文
    const row = minimumLevel({ 'Accept-Language': 'zh-CN' });

    expect(row.displayName).toBe('Minimum log level');
    expect(row.groupDisplayName).toBe('Operations');
  });
  //#endif
});
