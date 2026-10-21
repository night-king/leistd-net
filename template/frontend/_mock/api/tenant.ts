//#if (Impersonation)
import { requirePermission } from './authorization';
//#endif
import { TenantByHostOutputDto } from '../../src/app/features/account/dtos/tenant-by-host.dto';
//#if (Impersonation)
import { PERMISSIONS } from '../../src/app/shared/constants/permission.constants';
//#endif
import { PagedResultDto } from '../../src/app/shared/dtos/paged-result.dto';
import { MockException, MockRequest } from '../core/models';
import { ensureAcceptablePassword } from '../data/password-policy';
import {
  MockTenant,
  MockTenantConnection,
  TENANTS,
  toTenantConnection,
  toTenantOutput,
} from '../data/tenant';
//#if (Impersonation)
import { USERS } from '../data/user';
import {
  getCurrentUser,
  getMockImpersonator,
  getMockSessionTenantKey,
  setMockImpersonator,
  setMockSessionTenantKey,
  setMockSessionUserId,
} from '../utils/current-user';

/** 与后端 `AdminConstant.TenantAdminUsername` 一致：模拟登录进入的是租户里叫这个名字的用户。 */
const TENANT_ADMIN_USERNAME = 'admin';
//#endif

/** 连接名的合法形态，与后端 `TenantConnectionConfiguration.NamePattern` 同源。 */
const CONNECTION_NAME_PATTERN = /^[a-z0-9-]{1,64}$/;

function getQueryValue(value: unknown) {
  const normalized = Array.isArray(value) ? value[0] : value;
  return normalized === undefined || normalized === null || normalized === ''
    ? undefined
    : String(normalized);
}

export function getTenants(params: any): PagedResultDto<any> {
  let tenants = [...TENANTS];
  const offset = +(getQueryValue(params.offset) ?? 0);
  const limit = +(getQueryValue(params.limit) ?? 10);
  const keyword = getQueryValue(params.keyword)?.toLowerCase();

  if (keyword) {
    tenants = tenants.filter(
      (tenant) =>
        tenant.name.toLowerCase().includes(keyword) ||
        tenant.displayName?.toLowerCase().includes(keyword),
    );
  }

  // 与后端固定次序一致：创建时间倒序，同刻按 Id 倒序
  tenants.sort((a, b) => {
    const compared = b.creationTime.localeCompare(a.creationTime);
    return compared !== 0 ? compared : b.id.localeCompare(a.id);
  });

  return {
    totalCount: tenants.length,
    items: tenants.slice(offset, offset + limit).map(toTenantOutput),
  };
}

export function getTenantById(id: string) {
  const tenant = TENANTS.find((t) => t.id === id);
  if (!tenant) {
    throw new MockException(404, { code: 'Tenant:NotFound', message: 'Tenant not found' });
  }
  return toTenantOutput(tenant);
}

/** 按主机名探测租户（匿名）。Mock 没有子域名配置，恒回"域名不表态"，与真实后端在 localhost 下一致。 */
export function getTenantByHost(): TenantByHostOutputDto {
  return { decision: 'undecided' };
}

function findTenantOrThrow(tenantId: string): MockTenant {
  const tenant = TENANTS.find((t) => t.id === tenantId);
  if (!tenant) {
    throw new MockException(404, { code: 'Tenant:NotFound', message: 'Tenant not found' });
  }
  return tenant;
}

/** 连接名大小写不敏感：后端归一化为小写后存取，Mock 同样先归一化再匹配。 */
function normalizeConnectionName(name: string): string {
  return decodeURIComponent(name).trim().toLowerCase();
}

/**
 * 列出该租户已登记的连接。空数组即该租户不单独分库。
 *
 * 真实后端要求 App.Tenants.Update，Mock 不做权限，仅复刻投影形状。
 */
export function getTenantConnections(tenantId: string) {
  const tenant = findTenantOrThrow(tenantId);
  return tenant.connections.map((connection) => toTenantConnection(tenant, connection));
}

/** 登记或更新一条连接。`expectedVersion` 必须显式给出（首次传 `null`），后端对缺字段返回 400。 */
export function setTenantConnection(tenantId: string, rawName: string, value: any) {
  const tenant = findTenantOrThrow(tenantId);
  const name = normalizeConnectionName(rawName);
  if (!CONNECTION_NAME_PATTERN.test(name)) {
    throw new MockException(400, {
      code: 'TenantConnection:NameInvalid',
      message: 'Connection name must match ^[a-z0-9-]{1,64}$.',
    });
  }

  if (!value || !('expectedVersion' in value)) {
    throw new MockException(400, {
      message: 'Expected version is required; pass null for the first registration.',
    });
  }

  // 想让某个名字回到"用服务自己的库"，删掉这一条，而不是提交空连接串。
  if (!String(value.connectionString ?? '').trim()) {
    throw new MockException(400, {
      code: 'TenantConnection:ConnectionStringInvalid',
      message: 'Connection string is required.',
    });
  }

  const expectedVersion = value.expectedVersion === null ? null : Number(value.expectedVersion);
  const existing = tenant.connections.find((connection) => connection.name === name);

  // 首次登记预期这一条尚不存在，改已有的那条则必须带上读到的版本；两种落空都是 409。
  if (existing ? expectedVersion !== existing.version : expectedVersion !== null) {
    throw new MockException(409, {
      code: 'TenantConnection:VersionConflict',
      message: 'Tenant connection version mismatch',
    });
  }

  // 连接串不保存，只递增版本：真实后端加密存储且从不返回
  const connection = existing ?? { name, version: 0 };
  connection.version += 1;
  if (!existing) {
    tenant.connections.push(connection);
  }

  return toTenantConnection(tenant, connection);
}

/** 删除一条连接：这个名字之后回落到服务自己配置的数据库。 */
export function removeTenantConnection(
  tenantId: string,
  rawName: string,
  expectedVersion: string | undefined,
) {
  const tenant = findTenantOrThrow(tenantId);
  const name = normalizeConnectionName(rawName);
  const index = tenant.connections.findIndex((connection) => connection.name === name);
  if (index < 0) {
    throw new MockException(404, {
      message: 'Tenant connection not found',
    });
  }

  if (Number(expectedVersion) !== tenant.connections[index].version) {
    throw new MockException(409, {
      code: 'TenantConnection:VersionConflict',
      message: 'Tenant connection version mismatch',
    });
  }

  tenant.connections.splice(index, 1);
}

export function createTenant(value: any) {
  const name = String(value.name ?? '').trim();
  // 名称冲突复刻后端 409 形状。
  if (TENANTS.some((t) => t.name.toLowerCase() === name.toLowerCase())) {
    throw new MockException(409, {
      code: 'Tenant:DuplicateName',
      message: 'Tenant name already exists',
    });
  }
  // 复刻后端租户播种路径的口令策略（主体措辞与后端一致）。
  ensureAcceptablePassword(value.adminPassword, 'Tenant admin password');

  const newTenant = {
    id: crypto.randomUUID(),
    name,
    displayName: value.displayName?.trim() || undefined,
    description: value.description?.trim() || undefined,
    isActive: true,
    creationTime: new Date().toISOString(),
    // 分库在建租户这一步定案：连接与租户一起落库（后端是同一个事务），之后不能再补。
    // 名字与重名的校验复刻后端，否则 Mock 下能过、换真实后端才 400。
    connections: normalizeCreateConnections(value.connections),
  } satisfies MockTenant;
  TENANTS.push(newTenant);
  return toTenantOutput(newTenant);
}

// 复刻后端创建时对连接数组的整批校验：名字归一化后按模式校验、连接串非空、名字不得重复。
// 连接串只在校验时用一下就丢掉：Mock 不保存它（见 MockTenantConnection），存了迟早有人读出来回显。
function normalizeCreateConnections(raw: unknown): MockTenantConnection[] {
  const connections: MockTenantConnection[] = [];
  for (const entry of Array.isArray(raw) ? raw : []) {
    const name = normalizeConnectionName(String((entry as any)?.name ?? ''));
    if (!CONNECTION_NAME_PATTERN.test(name)) {
      throw new MockException(400, {
        code: 'TenantConnection:NameInvalid',
        message: 'Connection name must match ^[a-z0-9-]{1,64}$.',
      });
    }
    if (connections.some((connection) => connection.name === name)) {
      throw new MockException(400, {
        code: 'TenantConnection:NameDuplicated',
        message: `The connection name '${name}' was given more than once.`,
      });
    }
    const connectionString = String((entry as any)?.connectionString ?? '').trim();
    if (!connectionString) {
      throw new MockException(400, {
        code: 'TenantConnection:ConnectionStringInvalid',
        message: 'Connection string is required.',
      });
    }
    connections.push({ name, version: 1 });
  }
  return connections;
}

export function updateTenant(id: string, value: any) {
  const tenant = TENANTS.find((t) => t.id === id);
  if (!tenant) {
    throw new MockException(404, { code: 'Tenant:NotFound', message: 'Tenant not found' });
  }

  const name = String(value.name ?? '').trim();
  if (TENANTS.some((t) => t.id !== id && t.name.toLowerCase() === name.toLowerCase())) {
    throw new MockException(409, {
      code: 'Tenant:DuplicateName',
      message: 'Tenant name already exists',
    });
  }

  tenant.name = name;
  tenant.displayName = value.displayName?.trim() || undefined;
  // 整体覆盖，与后端一致：省略与显式 null 都清空描述，没有"不传即保留"这一档。
  tenant.description = value.description?.trim() || undefined;
  return toTenantOutput(tenant);
}

export function setTenantActivation(id: string, value: any) {
  const tenant = TENANTS.find((t) => t.id === id);
  if (!tenant) {
    throw new MockException(404, { code: 'Tenant:NotFound', message: 'Tenant not found' });
  }
  tenant.isActive = value.isActive === true;
  return toTenantOutput(tenant);
}

/** 删除幂等：与后端一致，不存在或已删除的租户直接成功。 */
export function deleteTenant(id: string) {
  const index = TENANTS.findIndex((t) => t.id === id);
  if (index >= 0) {
    TENANTS.splice(index, 1);
  }
}

//#if (Impersonation)
/**
 * 以租户管理员身份进入该租户：会话整体换成租户管理员，并记下发起人供顶栏提示与结束模拟。
 *
 * 复刻后端的拒绝条件：无权限 403、已在模拟中 409、租户不存在 404、已停用 403、
 * 租户里没有管理员 409。Mock 用户不按租户分区，租户管理员就是用户名为 admin 的那个。
 */
export function impersonateTenant(id: string): 'ok' {
  requirePermission(PERMISSIONS.tenants.impersonation);
  if (getMockImpersonator()) {
    throw new MockException(409, {
      code: 'Tenant:AlreadyImpersonating',
      message: 'Already impersonating; end the current impersonation first.',
    });
  }

  const tenant = findTenantOrThrow(id);
  if (!tenant.isActive) {
    throw new MockException(403, {
      code: 'Tenant:NotActive',
      message: `Tenant '${tenant.name}' is deactivated.`,
    });
  }

  const admin = USERS.find((user) => user.username === TENANT_ADMIN_USERNAME);
  if (!admin) {
    throw new MockException(409, {
      code: 'Tenant:AdministratorNotFound',
      message: `Tenant '${tenant.name}' has no '${TENANT_ADMIN_USERNAME}' user to impersonate.`,
    });
  }

  // requirePermission 已确认有会话用户
  const impersonator = getCurrentUser()!;
  setMockImpersonator({
    userId: impersonator.id,
    name: impersonator.displayName ?? impersonator.username,
    tenantKey: getMockSessionTenantKey(),
  });
  setMockSessionUserId(admin.id);
  setMockSessionTenantKey(tenant.id);
  return 'ok';
}

//#endif
export const TENANT_API = {
  'GET /api/v1/tenants': (req: MockRequest) => getTenants(req.queryParams),
  // by-host 必须排在 :id 之前，否则会被当成一个 id 走到按 id 查询那条上。
  'GET /api/v1/tenants/by-host': () => getTenantByHost(),
  'GET /api/v1/tenants/:id': (req: MockRequest) => getTenantById(req.params.id),
  'POST /api/v1/tenants': (req: MockRequest) => createTenant(req.body),
  'PUT /api/v1/tenants/:id/activation': (req: MockRequest) =>
    setTenantActivation(req.params.id, req.body),
  'PUT /api/v1/tenants/:id': (req: MockRequest) => updateTenant(req.params.id, req.body),
  'DELETE /api/v1/tenants/:id': (req: MockRequest) => deleteTenant(req.params.id),
  //#if (Impersonation)
  'POST /api/v1/tenants/:id/impersonate': (req: MockRequest) => impersonateTenant(req.params.id),
  //#endif
  'GET /api/v1/tenant-connections/:tenantId': (req: MockRequest) =>
    getTenantConnections(req.params.tenantId),
  'PUT /api/v1/tenant-connections/:tenantId/:name': (req: MockRequest) =>
    setTenantConnection(req.params.tenantId, req.params.name, req.body),
  'DELETE /api/v1/tenant-connections/:tenantId/:name': (req: MockRequest) =>
    removeTenantConnection(
      req.params.tenantId,
      req.params.name,
      getQueryValue(req.queryParams['expectedVersion']),
    ),
};
