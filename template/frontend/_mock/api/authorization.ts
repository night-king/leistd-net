import { PERMISSIONS } from '../../src/app/shared/constants/permission.constants';
import { PagedResultDto } from '../../src/app/shared/dtos/paged-result.dto';
import { MockException, MockRequest } from '../core/models';
import { MockSortFields, sortMockRows } from '../core/sorting';
import {
  ALL_PERMISSIONS,
  MockRole,
  PERMISSION_DEFINITIONS,
  PERMISSION_GRANTS,
  ROLES,
  grantKey,
} from '../data/authorization';
import { USERS } from '../data/user';
import { getCurrentUser } from '../utils/current-user';

/**
 * 角色与权限 Mock。
 *
 * 只复刻端点形状、401/403 和每个演示账号的固定权限集；多来源并集、写时祖先归一化
 * 与定义树遍历以后端集成测试为唯一事实来源，这里不重复实现。
 */

function requireUser() {
  const user = getCurrentUser();
  if (!user) {
    throw new MockException(401, { message: 'Not authenticated' });
  }
  return user;
}

/** 计算当前用户的有效权限：超管全量，否则取其角色授予的并集。 */
function effectivePermissionsOf(username: string): { permissions: string[]; versionToken: string } {
  const user = USERS.find((candidate) => candidate.username === username);
  if (!user) {
    return { permissions: [], versionToken: 'anonymous' };
  }

  if (user.isSuperAdmin) {
    return { permissions: [...ALL_PERMISSIONS], versionToken: 'super-admin' };
  }

  const granted = new Set<string>();
  const parts: string[] = [];

  for (const roleName of user.roles) {
    const role = ROLES.find((candidate) => candidate.name === roleName);
    if (!role) {
      continue;
    }

    const entry = PERMISSION_GRANTS[grantKey('Role', role.id)];
    parts.push(`${role.id}:${entry?.version ?? 0}`);
    for (const name of entry?.permissionNames ?? []) {
      granted.add(name);
    }
  }

  return {
    permissions: [...granted].sort(),
    versionToken: `r${parts.sort().join(',')}`,
  };
}

/** 端点级 403：与后端的策略保护逐个对应。 */
export function requirePermission(permission: string) {
  const user = requireUser();
  const { permissions } = effectivePermissionsOf(user.username);
  if (!permissions.includes(permission)) {
    throw new MockException(403, {
      message: `Missing permission: ${permission}`,
    });
  }
  return user;
}

export function getCurrentPermissions() {
  const user = requireUser();
  const { permissions, versionToken } = effectivePermissionsOf(user.username);
  const target = USERS.find((candidate) => candidate.username === user.username);
  return { permissions, isSuperAdmin: target?.isSuperAdmin ?? false, versionToken };
}

export function getPermissionDefinitions() {
  // 与后端一致：权限树只为授予而读，能配置角色权限即可读取。
  requireAnyPermission([PERMISSIONS.roles.managePermissions]);
  return PERMISSION_DEFINITIONS;
}

/** 端点级 403（任一满足）：与后端的多权限策略逐个对应。 */
function requireAnyPermission(candidates: string[]) {
  const user = requireUser();
  const { permissions } = effectivePermissionsOf(user.username);
  if (!candidates.some((permission) => permissions.includes(permission))) {
    throw new MockException(403, {
      message: `Missing any of: ${candidates.join(', ')}`,
    });
  }
  return user;
}

export function getRoles(params: Record<string, unknown>): PagedResultDto<unknown> {
  requirePermission('App.Roles');

  const offset = Number(params['offset'] ?? 0);
  const limit = Number(params['limit'] ?? 10);
  const keyword = String(params['keyword'] ?? '').toLowerCase();

  const matched = ROLES.filter(
    (role) =>
      !keyword ||
      role.name.toLowerCase().includes(keyword) ||
      role.displayName.toLowerCase().includes(keyword),
  ).map((role) => ({
    ...role,
    userCount: USERS.filter((user) => user.roles.includes(role.name)).length,
    permissionCount: PERMISSION_GRANTS[grantKey('Role', role.id)]?.permissionNames.length ?? 0,
  }));

  const ordered = sortRoles(matched, String(params['sorting'] ?? ''));
  const items = ordered.slice(offset, offset + limit);

  return { totalCount: matched.length, items };
}

type MockRoleRow = MockRole & { userCount: number; permissionCount: number };

const ROLE_SORT_FIELDS: MockSortFields<MockRoleRow> = {
  displayName: (role) => role.displayName,
  sort: (role) => role.sort,
  creationTime: (role) => Date.parse(role.creationTime),
  name: (role) => role.name,
  id: (role) => role.id,
};

function sortRoles(rows: MockRoleRow[], sorting: string): MockRoleRow[] {
  return sortMockRows(rows, sorting, ROLE_SORT_FIELDS, 'sort', 'name, id');
}

export function getRoleOptions() {
  requirePermission('App.Users.ManageRoles');
  // 与后端 GetAllAsync 同为 Sort → Name；排副本，原地排序会改动全局 Mock 数据。
  return [...ROLES]
    .sort((left, right) => left.sort - right.sort || left.name.localeCompare(right.name))
    .map((role) => ({
      id: role.id,
      name: role.name,
      displayName: role.displayName,
    }));
}

export function createRole(body: Record<string, unknown>) {
  requirePermission('App.Roles.Create');

  const name = String(body['name'] ?? '').trim();
  if (ROLES.some((role) => role.name === name)) {
    throw new MockException(409, {
      code: 'Role:NameAlreadyUsed',
      message: `Role '${name}' already exists.`,
    });
  }

  const role = {
    id: `role_${Date.now()}`,
    name,
    displayName: String(body['displayName'] ?? name),
    description: (body['description'] as string) || undefined,
    isStatic: false,
    isDefault: Boolean(body['isDefault']),
    sort: Number(body['sort'] ?? 0),
    creationTime: new Date().toISOString(),
  };
  ROLES.push(role);
  PERMISSION_GRANTS[grantKey('Role', role.id)] = { version: 0, permissionNames: [] };

  return { ...role, userCount: 0, permissionCount: 0 };
}

export function updateRole(id: string, body: Record<string, unknown>) {
  requirePermission('App.Roles.Update');

  const role = ROLES.find((candidate) => candidate.id === id);
  if (!role) {
    throw new MockException(404, { code: 'Role:NotFound', message: 'Role does not exist' });
  }

  role.displayName = String(body['displayName'] ?? role.displayName);
  role.description = (body['description'] as string) || undefined;
  role.sort = Number(body['sort'] ?? role.sort);
  role.isDefault = Boolean(body['isDefault']);
  role.lastModificationTime = new Date().toISOString();

  return {
    ...role,
    userCount: USERS.filter((user) => user.roles.includes(role.name)).length,
    permissionCount: PERMISSION_GRANTS[grantKey('Role', role.id)]?.permissionNames.length ?? 0,
  };
}

export function deleteRole(id: string) {
  requirePermission('App.Roles.Delete');

  // 与后端一致：删除幂等，不存在（含已删除）即成功
  const index = ROLES.findIndex((candidate) => candidate.id === id);
  if (index < 0) {
    return;
  }

  const role = ROLES[index];
  if (role.isStatic) {
    throw new MockException(409, {
      code: 'Role:StaticRoleCannotBeDeleted',
      message: `Built-in role '${role.name}' cannot be deleted.`,
    });
  }

  const assigned = USERS.filter((user) => user.roles.includes(role.name)).length;
  if (assigned > 0) {
    throw new MockException(409, {
      code: 'Role:RoleStillAssigned',
      message: `Role '${role.name}' still has ${assigned} assigned user(s).`,
    });
  }

  ROLES.splice(index, 1);
  delete PERMISSION_GRANTS[grantKey('Role', role.id)];
}

function buildGrantsResponse(providerName: string, providerKey: string) {
  const entry = PERMISSION_GRANTS[grantKey(providerName, providerKey)] ?? {
    version: 0,
    permissionNames: [],
  };
  const granted = new Set(entry.permissionNames);

  return {
    providerName,
    providerKey,
    version: entry.version,
    grants: ALL_PERMISSIONS.map((name) => ({ name, granted: granted.has(name) })),
  };
}

export function getRoleGrants(roleId: string) {
  requirePermission('App.Roles.ManagePermissions');
  return buildGrantsResponse('Role', roleId);
}

export function replaceRoleGrants(roleId: string, body: Record<string, unknown>) {
  requirePermission('App.Roles.ManagePermissions');

  const key = grantKey('Role', roleId);
  const entry = PERMISSION_GRANTS[key] ?? { version: 0, permissionNames: [] };
  const expected = Number(body['expectedVersion'] ?? 0);

  // 复刻乐观并发：版本不匹配返回 409，而不是静默覆盖对方的修改。
  if (expected !== entry.version) {
    throw new MockException(409, {
      code: 'Permission:ConcurrencyConflict',
      message: 'The permissions were changed by someone else. Reload and try again.',
    });
  }

  const permissionNames = (body['permissionNames'] as string[]) ?? [];
  const unknown = permissionNames.filter((name) => !ALL_PERMISSIONS.includes(name));
  if (unknown.length > 0) {
    throw new MockException(400, {
      code: 'Permission:UndefinedPermission',
      message: `Permission '${unknown[0]}' is not defined or is disabled.`,
    });
  }

  PERMISSION_GRANTS[key] = { version: entry.version + 1, permissionNames: [...permissionNames] };
  return buildGrantsResponse('Role', roleId);
}

export function getUserRoles(userId: string) {
  requirePermission('App.Users.ManageRoles');

  const user = USERS.find((candidate) => candidate.id === userId);
  if (!user) {
    throw new MockException(404, { code: 'User:NotFound', message: 'User does not exist' });
  }

  return ROLES.filter((role) => user.roles.includes(role.name)).map((role) => ({
    id: role.id,
    name: role.name,
    displayName: role.displayName,
  }));
}

/** 与后端一致：内置超级管理员的角色只有本人能改，别的管理员替换或追加都是 403。 */
export function ensureRolesCanBeManaged(user: (typeof USERS)[number]): void {
  if (user.isSuperAdmin && user.id !== getCurrentUser()?.id) {
    throw new MockException(403, {
      code: 'User:SuperAdminUpdateForbidden',
      message: 'The built-in super administrator cannot be updated by other administrators.',
    });
  }
}

export function replaceUserRoles(userId: string, body: Record<string, unknown>) {
  requirePermission('App.Users.ManageRoles');

  const user = USERS.find((candidate) => candidate.id === userId);
  if (!user) {
    throw new MockException(404, { code: 'User:NotFound', message: 'User does not exist' });
  }
  ensureRolesCanBeManaged(user);

  const roleIds = (body['roleIds'] as string[]) ?? [];
  user.roles = ROLES.filter((role) => roleIds.includes(role.id)).map((role) => role.name);

  return getUserRoles(userId);
}

export const AUTHORIZATION_API = {
  'GET /api/v1/permissions/current': () => getCurrentPermissions(),
  'GET /api/v1/permissions/definitions': () => getPermissionDefinitions(),
  'GET /api/v1/permissions/grants/roles/:roleId': (req: MockRequest) =>
    getRoleGrants(req.params.roleId),
  'PUT /api/v1/permissions/grants/roles/:roleId': (req: MockRequest) =>
    replaceRoleGrants(req.params.roleId, req.body),
  'GET /api/v1/roles': (req: MockRequest) => getRoles(req.queryParams),
  'GET /api/v1/roles/options': () => getRoleOptions(),
  'POST /api/v1/roles': (req: MockRequest) => createRole(req.body),
  'PUT /api/v1/roles/:id': (req: MockRequest) => updateRole(req.params.id, req.body),
  'DELETE /api/v1/roles/:id': (req: MockRequest) => deleteRole(req.params.id),
  'GET /api/v1/users/:id/roles': (req: MockRequest) => getUserRoles(req.params.id),
  'PUT /api/v1/users/:id/roles': (req: MockRequest) => replaceUserRoles(req.params.id, req.body),
};
