import { ensureRolesCanBeManaged, requirePermission } from './authorization';
import { PagedResultDto } from '../../src/app/shared/dtos/paged-result.dto';
import { MockException, MockRequest } from '../core/models';
import { MockSortFields, sortMockRows } from '../core/sorting';
import { ROLES } from '../data/authorization';
//#if (LocalIdentity)
import { ensureAcceptablePassword } from '../data/password-policy';
//#endif
// prettier-ignore
import {
  //#if (LocalIdentity)
  DELETED_USERS,
  //#endif
  USERS,
  //#if (LocalIdentity)
  isEmailTaken,
  isUsernameTaken,
  //#endif
  toUserManagementOutput,
} from '../data/user';
//#if (LocalIdentity)
import {
  ensureMockReauthentication,
  getCurrentUser,
  MockReauthenticationProof,
} from '../utils/current-user';
//#else
import { getCurrentUser } from '../utils/current-user';
//#endif

function getQueryValue(value: unknown) {
  const normalized = Array.isArray(value) ? value[0] : value;
  return normalized === undefined || normalized === null || normalized === ''
    ? undefined
    : String(normalized);
}

const USER_SORT_FIELDS: MockSortFields<(typeof USERS)[number]> = {
  username: (user) => user.username,
  email: (user) => user.email,
  creationTime: (user) => Date.parse(user.creationTime),
  id: (user) => user.id,
  //#if (LocalIdentity)
  'lastLogin.Time': (user) => (user.lastLoginTime ? Date.parse(user.lastLoginTime) : null),
  //#endif
};

function sortUsers(users: typeof USERS, sorting?: string) {
  return sortMockRows(users, sorting, USER_SORT_FIELDS, 'creationTime desc', 'id');
}

export function getUsers(params: any): PagedResultDto<any> {
  let users = [...USERS];
  const offset = +(getQueryValue(params.offset) ?? 0);
  const limit = +(getQueryValue(params.limit) ?? 10);
  const keyword = getQueryValue(params.keyword)?.toLowerCase();
  const isActive = getQueryValue(params.isActive);
  const isEmailVerified = getQueryValue(params.isEmailVerified);
  const rolesParam = params.roles;
  const roles: string[] = Array.isArray(rolesParam) ? rolesParam : rolesParam ? [rolesParam] : [];
  const sorting = getQueryValue(params.sorting);

  if (keyword) {
    users = users.filter(
      (user) =>
        user.username.toLowerCase().includes(keyword) ||
        user.email.toLowerCase().includes(keyword) ||
        user.displayName?.toLowerCase().includes(keyword),
    );
  }

  if (isActive !== undefined) {
    users = users.filter((user) => user.isActive === (isActive === 'true'));
  }

  if (isEmailVerified !== undefined) {
    users = users.filter((user) => user.isEmailVerified === (isEmailVerified === 'true'));
  }

  if (roles.length) {
    users = users.filter((user) => user.roles.some((r) => roles.includes(r)));
  }

  users = sortUsers(users, sorting);

  return {
    totalCount: users.length,
    items: users.slice(offset, offset + limit).map(toUserManagementOutput),
  };
}

export function getUserById(id: string) {
  const user = USERS.find((w) => w.id === id);
  if (!user) {
    throw new MockException(404, { code: 'User:NotFound', message: 'User not found' });
  }
  return toUserManagementOutput(user);
}

//#if (LocalIdentity)
export function addUser(value: any) {
  const username = String(value.username ?? '').trim();
  const email = String(value.email ?? '').trim();
  // 与后端一致：已删除用户仍占着用户名与邮箱
  if (isUsernameTaken(username)) {
    throw new MockException(409, {
      code: 'User:UsernameTaken',
      message: `Username '${username}' already exists.`,
    });
  }
  if (isEmailTaken(email)) {
    throw new MockException(409, {
      code: 'User:EmailTaken',
      message: `Email '${email}' is already in use.`,
    });
  }
  // 复刻后端口令策略：创建用户必须显式给出合规口令，没有默认值。
  ensureAcceptablePassword(value.password, 'Password');

  const newUser = {
    id: crypto.randomUUID(),
    username,
    email,
    displayName: value.displayName,
    avatar:
      value.avatar ||
      `https://api.dicebear.com/7.x/avataaars/svg?seed=${encodeURIComponent(username)}`,
    phoneNumber: value.phoneNumber,
    isActive: value.isActive ?? true,
    isSuperAdmin: false,
    isEmailVerified: value.isEmailVerified ?? false,
    creationTime: new Date().toISOString(),
    // 创建时按 Id 提交角色；未指定则落到默认角色，与后端一致。
    roles: (value.roleIds?.length
      ? ROLES.filter((role: { id: string }) => value.roleIds.includes(role.id)).map(
          (role: { name: string }) => role.name,
        )
      : ROLES.filter((role: { isDefault: boolean }) => role.isDefault).map(
          (role: { name: string }) => role.name,
        )) as string[],
    password: value.password,
  };
  USERS.push(newUser);
  return toUserManagementOutput(newUser);
}

export function updateUser(id: string, value: any) {
  const user = USERS.find((w) => w.id === id);
  if (!user) {
    throw new MockException(404, {
      code: 'User:NotFound',
      message: 'User does not exist or has been deleted',
    });
  }
  // 与后端一致：邮箱先去掉首尾空白再判占用与保存；被其他用户占用时 409，不静默覆盖
  const email = String(value.email ?? '').trim();
  if (email && isEmailTaken(email, id)) {
    throw new MockException(409, {
      code: 'User:EmailTaken',
      message: `Email '${email}' is already in use.`,
    });
  }

  // 改登录邮箱或改验证状态会影响对方怎么登录：先确认操作者本人
  if (email !== user.email || value.isEmailVerified !== user.isEmailVerified) {
    ensureOperatorReauthenticated({ password: value.password, twoFactorCode: value.twoFactorCode });
  }

  Object.assign(user, {
    email,
    displayName: value.displayName,
    avatar: value.avatar,
    isActive: value.isActive,
    isEmailVerified: value.isEmailVerified,
    // 普通更新不接受角色：角色分配是独立命令，走 PUT /api/v1/users/:id/roles。
  });
  return toUserManagementOutput(user);
}

//#endif
export function enableUser(id: string) {
  const user = USERS.find((w) => w.id === id);
  if (!user) {
    throw new MockException(404, {
      code: 'User:NotFound',
      message: 'User does not exist or has been deleted',
    });
  }
  user.isActive = true;
}

/** 与后端一致：内置超级管理员不可被停用（403），任何人都不能停用自己（400）；拒绝时不改任何状态。 */
export function disableUser(id: string) {
  const user = USERS.find((w) => w.id === id);
  if (!user) {
    throw new MockException(404, {
      code: 'User:NotFound',
      message: 'User does not exist or has been deleted',
    });
  }
  const self = user.id === getCurrentUser()?.id;
  if (user.isSuperAdmin) {
    throw new MockException(403, {
      code: self ? 'User:SuperAdminDisableSelfForbidden' : 'User:SuperAdminDisableForbidden',
      message: self
        ? 'The built-in super administrator cannot disable itself.'
        : 'The built-in super administrator cannot be disabled by other administrators.',
    });
  }
  if (self) {
    throw new MockException(400, {
      code: 'User:SelfDisableForbidden',
      message: 'You cannot disable your own account.',
    });
  }
  user.isActive = false;
}

//#if (LocalIdentity)
export function resetPassword(id: string, value: any) {
  const user = USERS.find((w) => w.id === id);
  if (!user) {
    throw new MockException(404, {
      code: 'User:NotFound',
      message: 'User does not exist or has been deleted',
    });
  }
  if (user.isSuperAdmin) {
    throw new MockException(403, {
      code: 'User:SuperAdminResetPasswordForbidden',
      message:
        "The built-in super administrator's password cannot be reset by other administrators.",
    });
  }
  ensureAcceptablePassword(value.password, 'Password');
  ensureOperatorReauthenticated({
    password: value.currentPassword,
    twoFactorCode: value.twoFactorCode,
  });
  user.password = value.password;
}

export function unlockUser(id: string) {
  const user = USERS.find((w) => w.id === id);
  if (!user) {
    throw new MockException(404, {
      code: 'User:NotFound',
      message: 'User does not exist or has been deleted',
    });
  }
  delete user.lockoutEnd;
}

/**
 * 管理员重置两步验证。超级管理员只能由本人重置（403）；未启用时什么也不做，与后端一致。
 * 真实后端还会撤销该用户的全部会话，Mock 的会话列表只有当前用户自己的，这里不复刻。
 */
export function resetTwoFactor(id: string, value: MockReauthenticationProof = {}) {
  const user = USERS.find((w) => w.id === id);
  if (!user) {
    throw new MockException(404, {
      code: 'User:NotFound',
      message: `User ${id} not found.`,
    });
  }
  if (user.isSuperAdmin && user.id !== getCurrentUser()?.id) {
    throw new MockException(403, {
      code: 'User:SuperAdminOperationForbidden',
      message: 'The built-in super administrator cannot be operated on by other administrators.',
    });
  }
  // 与后端一致：本人走个人设置的换绑或停用，管理入口不放行
  if (user.id === getCurrentUser()?.id) {
    throw new MockException(400, {
      code: 'User:SelfTwoFactorResetForbidden',
      message:
        "You can't reset your own two-factor authentication here. Use your security settings.",
    });
  }
  ensureOperatorReauthenticated(value);
  user.twoFactorEnabled = false;
  user.recoveryCodes = [];
}

/**
 * 管理员改他人的登录凭据前确认的是操作者本人（出示自己的口令或验证码），不是目标用户。
 * 未登录由权限层拒绝，这里不重复。
 */
function ensureOperatorReauthenticated(proof: MockReauthenticationProof): void {
  const operator = getCurrentUser();
  if (operator) {
    ensureMockReauthentication(operator, proof);
  }
}

/**
 * 与后端一致：删除幂等，不存在（含已删除）即成功；内置超级管理员不可删（403），不能删除自己（400）。
 * 删除是软删除：移出列表但仍占着用户名与邮箱。
 */
export function deleteUser(id: string) {
  const index = USERS.findIndex((w) => w.id === id);
  if (index < 0) {
    return;
  }
  if (USERS[index].isSuperAdmin) {
    throw new MockException(403, {
      code: 'User:SuperAdminDeleteForbidden',
      message: 'The built-in super administrator cannot be deleted',
    });
  }
  if (USERS[index].id === getCurrentUser()?.id) {
    throw new MockException(400, {
      code: 'User:SelfDeleteForbidden',
      message: 'You cannot delete your own account.',
    });
  }
  const [deleted] = USERS.splice(index, 1);
  DELETED_USERS.push({ ...deleted, deletionTime: new Date().toISOString() });
}

//#endif
/**
 * 与后端批量入参校验同形：400 字段错误。数量按提交的原数组校验（后端在去重之前验证），
 * 通过后重复的只处理一次。
 */
function distinctItems(body: Record<string, unknown>, field: string, max: number): string[] {
  const items = Array.isArray(body[field]) ? body[field].map(String) : [];
  if (items.length < 1 || items.length > max) {
    throw new MockException(400, {
      errors: [{ field, detail: `${field} must contain between 1 and ${max} items.` }],
    });
  }
  return [...new Set(items)];
}

function batchIds(body: Record<string, unknown>): string[] {
  return distinctItems(body, 'ids', 100);
}

/** 逐条执行，复用单条操作的规则；失败的条目带单条接口同样的错误码与说明，不回滚已成功的。 */
function runBatch(ids: string[], operation: (id: string) => void) {
  return {
    items: ids.map((id) => {
      try {
        operation(id);
        return { id, succeeded: true };
      } catch (error) {
        const detail = error instanceof MockException ? error.error : undefined;
        return { id, succeeded: false, errorCode: detail?.code, message: detail?.message };
      }
    }),
  };
}

export function batchEnableUsers(body: Record<string, unknown>) {
  requirePermission('App.Users.Update');
  return runBatch(batchIds(body), enableUser);
}

export function batchDisableUsers(body: Record<string, unknown>) {
  requirePermission('App.Users.Update');
  return runBatch(batchIds(body), disableUser);
}
//#if (LocalIdentity)

export function batchUnlockUsers(body: Record<string, unknown>) {
  requirePermission('App.Users.Update');
  return runBatch(batchIds(body), unlockUser);
}
//#endif

/** 批量追加角色：只追加不覆盖；角色 1–20 个，有不存在的整批 400，一条也不执行。 */
export function batchAssignUserRoles(body: Record<string, unknown>) {
  requirePermission('App.Users.ManageRoles');
  const ids = batchIds(body);
  const roleIds = distinctItems(body, 'roleIds', 20);
  const roles = ROLES.filter((role) => roleIds.includes(role.id));
  if (roles.length !== roleIds.length) {
    throw new MockException(400, {
      code: 'User:RolesNotFound',
      message: 'One or more roles do not exist.',
    });
  }

  return runBatch(ids, (id) => {
    const user = USERS.find((candidate) => candidate.id === id);
    if (!user) {
      throw new MockException(404, { code: 'User:NotFound', message: 'User does not exist' });
    }
    ensureRolesCanBeManaged(user);
    user.roles = [...new Set([...user.roles, ...roles.map((role) => role.name)])];
  });
}

export const USER_API = {
  'GET /api/v1/users': (req: MockRequest) => getUsers(req.queryParams),
  'GET /api/v1/users/:id': (req: MockRequest) => getUserById(req.params.id),
  //#if (LocalIdentity)
  'POST /api/v1/users': (req: MockRequest) => addUser(req.body),
  'PUT /api/v1/users/:id': (req: MockRequest) => updateUser(req.params.id, req.body),
  //#endif
  'PATCH /api/v1/users/:id/enable': (req: MockRequest) => enableUser(req.params.id),
  'PATCH /api/v1/users/:id/disable': (req: MockRequest) => disableUser(req.params.id),
  'POST /api/v1/users/batch/enable': (req: MockRequest) => batchEnableUsers(req.body),
  'POST /api/v1/users/batch/disable': (req: MockRequest) => batchDisableUsers(req.body),
  'POST /api/v1/users/batch/assign-roles': (req: MockRequest) => batchAssignUserRoles(req.body),
  //#if (LocalIdentity)
  'POST /api/v1/users/:id/reset-password': (req: MockRequest) =>
    resetPassword(req.params.id, req.body),
  'POST /api/v1/users/:id/unlock': (req: MockRequest) => unlockUser(req.params.id),
  'POST /api/v1/users/batch/unlock': (req: MockRequest) => batchUnlockUsers(req.body),
  'POST /api/v1/users/:id/reset-two-factor': (req: MockRequest) =>
    resetTwoFactor(req.params.id, req.body ?? {}),
  'DELETE /api/v1/users/:id': (req: MockRequest) => deleteUser(req.params.id),
  //#endif
};
