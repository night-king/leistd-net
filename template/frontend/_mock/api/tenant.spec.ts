import {
  createTenant,
  deleteTenant,
  getTenantByHost,
  getTenantById,
  getTenantConnections,
  removeTenantConnection,
  setTenantConnection,
  TENANT_API,
  updateTenant,
} from './tenant';
import { MockException } from '../core/models';
import { MockTenant, TENANTS } from '../data/tenant';
//#if (Impersonation)
import {
  MOCK_SESSION_USER_ID,
  getMockImpersonator,
  getMockSessionTenantKey,
  setMockSessionTenantKey,
  setMockSessionUserId,
} from '../utils/current-user';
//#endif

/**
 * Mock 与真实后端的契约对齐：Mock 是 `useMock` 模式下的后端，契约加了字段而 Mock 没跟上时，
 * 只在 Mock 模式下表现为"填了保存、值没了"。
 */
describe('tenant mock', () => {
  let snapshot: MockTenant[];

  beforeEach(() => {
    // 连接数组必须逐条复制：浅拷贝下 connections 仍是同一个数组，
    // 一个用例登记的连接会漏给下一个用例，断言"列表为空"就再也测不准了。
    snapshot = TENANTS.map((tenant) => ({
      ...tenant,
      connections: tenant.connections.map((connection) => ({ ...connection })),
    }));
  });

  afterEach(() => {
    TENANTS.length = 0;
    TENANTS.push(...snapshot);
  });

  function create(description?: string) {
    return createTenant({
      name: `probe-${Math.random().toString(36).slice(2, 8)}`,
      displayName: 'Probe Inc.',
      description,
      adminEmail: 'probe@example.test',
      adminPassword: 'MockTenant!Adm1n',
    });
  }

  /** 断言抛出的是带指定状态码的 MockException——状态码就是契约的一部分。 */
  function expectStatus(action: () => unknown, status: number): void {
    let thrown: unknown;
    try {
      action();
    } catch (error) {
      thrown = error;
    }

    expect(thrown).toBeInstanceOf(MockException);
    expect((thrown as MockException).status).toBe(status);
  }

  it('persists the description given on create and returns it on read', () => {
    const created = create('华东区自营资金账户');

    expect(created.description).toBe('华东区自营资金账户');
    expect(getTenantById(created.id).description).toBe('华东区自营资金账户');
  });

  it('can change the description on update', () => {
    const created = create('原始描述');

    const updated = updateTenant(created.id, { name: created.name, description: '改过的描述' });

    expect(updated.description).toBe('改过的描述');
  });

  // 与后端一致：PUT 是整体覆盖，省略描述等于清空，没有"不传即保留原值"这一档。
  // 两者不一致时，Mock 模式下会看到"清空没生效"，真实后端下却生效。
  it('clears the description when it is omitted on update', () => {
    const created = create('原始描述');

    expect(updateTenant(created.id, { name: created.name }).description).toBeUndefined();
    expect(getTenantById(created.id).description).toBeUndefined();
  });

  it('treats a blank description as cleared', () => {
    const created = create('原始描述');

    expect(
      updateTenant(created.id, { name: created.name, description: '   ' }).description,
    ).toBeUndefined();
  });

  // 路由顺序：by-host 必须排在 :id 之前，否则会被当成一个 id 走错分支。
  it('registers the by-host probe and the three connection routes, with the probe before the by-id lookup', () => {
    const routes = Object.keys(TENANT_API);

    expect(routes).toContain('GET /api/v1/tenants/by-host');
    expect(routes).toContain('GET /api/v1/tenant-connections/:tenantId');
    expect(routes).toContain('PUT /api/v1/tenant-connections/:tenantId/:name');
    expect(routes).toContain('DELETE /api/v1/tenant-connections/:tenantId/:name');
    expect(routes.indexOf('GET /api/v1/tenants/by-host')).toBeLessThan(
      routes.indexOf('GET /api/v1/tenants/:id'),
    );
  });

  // 本机开发不是任何受管域，真实后端在同一条件下也回"域名不表态"——
  // 登录页据此保留记住的租户并允许手选。
  it('returns undecided from the by-host probe', () => {
    expect(getTenantByHost()).toEqual({ decision: 'undecided' });
  });

  // 新建的租户一条登记都没有，这一档就是"不单独分库，各服务用自己配置的库"。
  it('returns an empty connection list for a new tenant', () => {
    expect(getTenantConnections(create().id)).toEqual([]);
  });

  it('lists a registered connection, starting at version 1', () => {
    const created = create();

    const connection = setTenantConnection(created.id, 'crm', {
      expectedVersion: null,
      connectionString: 'Host=db;Database=probe;Password=probe-secret',
    });

    expect(connection).toEqual({ tenantId: created.id, name: 'crm', version: 1 });
    expect(getTenantConnections(created.id)).toEqual([
      { tenantId: created.id, name: 'crm', version: 1 },
    ]);
  });

  // 名字归一化为小写：填 Crm 与 crm 命中同一条，不会变成看着两条、写进去一条。
  it('normalizes connection names to lowercase', () => {
    const created = create();

    expect(
      setTenantConnection(created.id, 'Crm', {
        expectedVersion: null,
        connectionString: 'Host=db;Password=probe-secret',
      }).name,
    ).toBe('crm');

    // 同一个名字再首次登记一次，预期"这条不存在"已经落空
    expectStatus(
      () =>
        setTenantConnection(created.id, 'crm', {
          expectedVersion: null,
          connectionString: 'Host=db;Password=probe-secret',
        }),
      409,
    );
  });

  it('rejects an update on a version mismatch', () => {
    const created = create();
    setTenantConnection(created.id, 'crm', {
      expectedVersion: null,
      connectionString: 'Host=db;Password=probe-secret',
    });

    expectStatus(
      () =>
        setTenantConnection(created.id, 'crm', {
          expectedVersion: 99,
          connectionString: 'Host=db;Password=probe-secret',
        }),
      409,
    );
    expect(getTenantConnections(created.id)[0].version).toBe(1);
  });

  it('treats deleting a missing or already deleted tenant as success', () => {
    const created = create();

    deleteTenant(created.id);
    deleteTenant(created.id);
    deleteTenant('00000000-0000-0000-0000-000000000000');

    expectStatus(() => getTenantById(created.id), 404);
  });

  it('empties the list after deletion and does not delete on a version mismatch', () => {
    const created = create();
    const connection = setTenantConnection(created.id, 'crm', {
      expectedVersion: null,
      connectionString: 'Host=db;Password=probe-secret',
    });

    expectStatus(() => removeTenantConnection(created.id, 'crm', '99'), 409);
    expect(getTenantConnections(created.id).length).toBe(1);

    removeTenantConnection(created.id, 'crm', String(connection.version));

    expect(getTenantConnections(created.id)).toEqual([]);
  });

  // 连接串只写：Mock 不保存它，登记返回、列表与库里的数据都不能出现它
  it('keeps connection strings out of the mock data and every response', () => {
    const created = create();

    const connection = setTenantConnection(created.id, 'crm', {
      expectedVersion: null,
      connectionString: 'Host=db;Database=probe;Password=probe-secret',
    });

    const stored = TENANTS.find((tenant) => tenant.id === created.id);
    expect(JSON.stringify(connection)).not.toContain('probe-secret');
    expect(JSON.stringify(getTenantConnections(created.id))).not.toContain('probe-secret');
    expect(JSON.stringify(stored)).not.toContain('probe-secret');
  });

  // 分库在建租户这一步定案：连接与租户一起落库，之后不能再补。
  it('registers all named connections given on tenant creation at once', () => {
    const created = createTenant({
      name: `probe-${Math.random().toString(36).slice(2, 8)}`,
      adminEmail: 'probe@example.test',
      adminPassword: 'MockTenant!Adm1n',
      connections: [
        { name: 'default', connectionString: 'Host=db;Database=probe;Password=probe-secret' },
        { name: 'CRM', connectionString: 'Host=db;Database=probe-crm;Password=probe-secret' },
      ],
    });

    // 名字归一化为小写，与后端一致
    expect(
      getTenantConnections(created.id)
        .map((connection) => connection.name)
        .sort(),
    ).toEqual(['crm', 'default']);
    // 连接串既不回显，也不落进 Mock 的内部数据——存了迟早有人把它读出来
    expect(JSON.stringify(getTenantConnections(created.id))).not.toContain('probe-secret');
    expect(JSON.stringify(created)).not.toContain('probe-secret');
    expect(JSON.stringify(TENANTS.find((tenant) => tenant.id === created.id))).not.toContain(
      'probe-secret',
    );
  });

  it('returns 400 for two connections with the same name on tenant creation', () => {
    expectStatus(
      () =>
        createTenant({
          name: `probe-${Math.random().toString(36).slice(2, 8)}`,
          adminEmail: 'probe@example.test',
          adminPassword: 'MockTenant!Adm1n',
          connections: [
            { name: 'crm', connectionString: 'Host=a' },
            { name: 'CRM', connectionString: 'Host=b' },
          ],
        }),
      400,
    );
  });
});
//#if (Impersonation)

/**
 * 模拟登录：拒绝时会话原样保留（不能"拒绝了却已经换了身份"），成功时会话整体换成租户管理员、
 * 并记下发起人——顶栏提示与结束模拟都只认这一份。
 */
describe('tenant impersonation mock', () => {
  const impersonate = TENANT_API['POST /api/v1/tenants/:id/impersonate'] as (req: {
    params: { id: string };
  }) => unknown;

  afterEach(() => setMockSessionUserId(null));

  function errorOf(action: () => unknown): MockException {
    try {
      action();
    } catch (error) {
      expect(error).toBeInstanceOf(MockException);
      return error as MockException;
    }
    throw new Error('expected the handler to reject');
  }

  function signIn(userId: string): void {
    setMockSessionUserId(userId);
    setMockSessionTenantKey('host');
  }

  it('rejects an anonymous caller with 401', () => {
    expect(errorOf(() => impersonate({ params: { id: 'tenant_acme' } })).status).toBe(401);
    expect(getMockImpersonator()).toBeNull();
  });

  it('rejects a user without the impersonation permission with 403 and keeps the session', () => {
    signIn('user_demo');

    expect(errorOf(() => impersonate({ params: { id: 'tenant_acme' } })).status).toBe(403);

    expect(MOCK_SESSION_USER_ID).toBe('user_demo');
    expect(getMockSessionTenantKey()).toBe('host');
    expect(getMockImpersonator()).toBeNull();
  });

  it('rejects a missing tenant with 404 and a deactivated one with 403', () => {
    signIn('user_admin');

    const missing = errorOf(() => impersonate({ params: { id: 'tenant_missing' } }));
    expect(missing.status).toBe(404);
    expect(missing.error.code).toBe('Tenant:NotFound');

    const inactive = errorOf(() => impersonate({ params: { id: 'tenant_globex' } }));
    expect(inactive.status).toBe(403);
    expect(inactive.error.code).toBe('Tenant:NotActive');

    expect(getMockSessionTenantKey()).toBe('host');
    expect(getMockImpersonator()).toBeNull();
  });

  it('enters the tenant as its admin, records the impersonator and refuses nesting', () => {
    signIn('user_admin');

    impersonate({ params: { id: 'tenant_acme' } });

    expect(MOCK_SESSION_USER_ID).toBe('user_admin');
    expect(getMockSessionTenantKey()).toBe('tenant_acme');
    expect(getMockImpersonator()).toEqual({
      userId: 'user_admin',
      name: 'Administrator',
      tenantKey: 'host',
    });

    const nested = errorOf(() => impersonate({ params: { id: 'tenant_acme' } }));
    expect(nested.status).toBe(409);
    expect(nested.error.code).toBe('Tenant:AlreadyImpersonating');
  });
});
//#endif
