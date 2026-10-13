import { ROLES } from './authorization';
import { UserManagementOutputDto } from '../../src/app/features/platform/dtos/user-management.dto';
import { UserOutputDto } from '../../src/app/shared/dtos/auth.dto';

export interface MockUser {
  id: string;
  username: string;
  email: string;
  password: string;
  displayName?: string;
  avatar?: string;
  phoneNumber?: string;
  isActive: boolean;
  isSuperAdmin: boolean;
  isEmailVerified: boolean;
  creationTime: string;
  /** 软删除时刻；只有 DELETED_USERS 里的用户有值。统计按它回推历史总数。 */
  deletionTime?: string;
  lastLoginTime?: string;
  /** 登录锁定：有值即锁定中；null 表示无期限（管理员锁定）。 */
  lockoutEnd?: string | null;
  //#if (LocalIdentity)
  /** 已启用两步验证；登录第二步、管理员重置都读写这一项。 */
  twoFactorEnabled?: boolean;
  /** 尚未用过的恢复码，个数即"剩余恢复码"。 */
  recoveryCodes?: string[];
  //#endif
  roles: string[];
}

export const USERS: MockUser[] = [
  {
    id: 'user_admin',
    username: 'admin',
    email: 'admin@example.com',
    password: 'Admin@123456',
    displayName: 'Administrator',
    avatar: 'https://api.dicebear.com/7.x/avataaars/svg?seed=admin',
    isActive: true,
    isSuperAdmin: true,
    isEmailVerified: true,
    creationTime: '2025-01-01T00:00:00Z',
    lastLoginTime: '2026-06-10T08:00:00Z',
    roles: ['Admin'],
  },
  {
    id: 'user_demo',
    username: 'demo',
    email: 'demo@example.com',
    password: 'Demo@123456',
    displayName: 'Demo User',
    avatar: 'https://api.dicebear.com/7.x/avataaars/svg?seed=demo',
    isActive: true,
    isSuperAdmin: false,
    isEmailVerified: true,
    creationTime: '2025-06-01T00:00:00Z',
    lastLoginTime: '2026-06-09T12:00:00Z',
    roles: ['Member'],
  },
];
//#if (LocalIdentity)

/**
 * 已删除的用户：不出现在任何列表与查询里，但仍占着用户名和邮箱。
 * 与后端一致：唯一索引不排除软删除行，查重关掉了软删除过滤。
 */
export const DELETED_USERS: MockUser[] = [];

/** 用户名是否已被占用（含已删除用户），按唯一索引原样比较。 */
export function isUsernameTaken(username: string, excludeUserId?: string): boolean {
  return [...USERS, ...DELETED_USERS].some(
    (user) => user.username === username && user.id !== excludeUserId,
  );
}

/** 邮箱是否已被占用（含已删除用户），按唯一索引原样比较，只差大小写的是另一个地址。 */
export function isEmailTaken(email: string, excludeUserId?: string): boolean {
  return [...USERS, ...DELETED_USERS].some(
    (user) => user.email === email && user.id !== excludeUserId,
  );
}
//#endif

export function toUserOutput(user: MockUser): UserOutputDto {
  return {
    id: user.id,
    username: user.username,
    email: user.email,
    displayName: user.displayName,
    avatar: user.avatar,
    phoneNumber: user.phoneNumber,
    //#if (LocalIdentity)
    isActive: user.isActive,
    //#endif
    isEmailVerified: user.isEmailVerified,
    isSuperAdmin: user.isSuperAdmin,
    //#if (LocalIdentity)
    creationTime: user.creationTime,
    //#endif
    // 当前用户模型只需要角色名（用于展示徽章），不需要 Id。
    roles: user.roles,
  };
}

export function toUserManagementOutput(user: MockUser): UserManagementOutputDto {
  return {
    id: user.id,
    username: user.username,
    email: user.email,
    displayName: user.displayName,
    avatar: user.avatar,
    isActive: user.isActive,
    //#if (LocalIdentity)
    isEmailVerified: user.isEmailVerified,
    //#endif
    // 角色以 Id + 名称的结构返回：Id 用于提交，名称仅用于展示与筛选。
    roles: ROLES.filter((role) => user.roles.includes(role.name)).map((role) => ({
      id: role.id,
      name: role.name,
      displayName: role.displayName,
    })),
    isSuperAdmin: user.isSuperAdmin,
    creationTime: user.creationTime,
    //#if (LocalIdentity)
    lastLoginTime: user.lastLoginTime,
    isLockedOut:
      user.lockoutEnd === null ||
      (user.lockoutEnd !== undefined && new Date(user.lockoutEnd).getTime() > Date.now()),
    lockoutEnd: user.lockoutEnd ?? null,
    isTwoFactorEnabled: user.twoFactorEnabled === true,
    //#endif
  };
}
