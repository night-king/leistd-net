import { ROLES } from './authorization';
import { UserManagementOutputDto } from '../../src/app/features/platform/dtos/user-management.dto';
import { UserOutputDto } from '../../src/app/shared/dtos/auth.dto';

export interface MockUser {
  id: string;
  username: string;
  email: string;
  /** 本地口令；只经外部登录建立的账号没有。 */
  password?: string;
  /** 安全版本（对应后端 SecurityStamp）：Mock 只在口令变更时递增，绑定它的找回挑战随之作废。 */
  securityVersion?: number;
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
    //#if (LocalIdentity)
    // 已开两步验证、恢复码所剩无几：演示登录第二步与"恢复码不多"的提醒
    twoFactorEnabled: true,
    recoveryCodes: ['mock-0000-code-demo', 'mock-0001-code-demo'],
    //#endif
    roles: ['Member'],
  },
  // 以下几位覆盖列表里的各种状态（多角色、锁定、停用、邮箱未验证），截图与演示不至于只有两行
  {
    id: 'user_jiayi',
    username: 'jiayi.lin',
    email: 'jiayi.lin@example.com',
    password: 'Jiayi@123456',
    displayName: '林嘉怡',
    isActive: true,
    isSuperAdmin: false,
    isEmailVerified: true,
    creationTime: '2025-03-02T00:00:00Z',
    lastLoginTime: '2026-06-12T01:41:00Z',
    roles: ['Admin', 'Member'],
  },
  {
    id: 'user_zihang',
    username: 'zihang',
    email: 'zihang.zhou@example.com',
    password: 'Zihang@123456',
    displayName: '周子航',
    isActive: true,
    isSuperAdmin: false,
    isEmailVerified: true,
    creationTime: '2025-04-18T00:00:00Z',
    lastLoginTime: '2026-06-12T00:15:00Z',
    roles: ['Operator', 'Member'],
  },
  {
    id: 'user_siyuan',
    username: 'siyuan.c',
    email: 'siyuan@example.org',
    password: 'Siyuan@123456',
    displayName: '陈思远',
    isActive: true,
    isSuperAdmin: false,
    isEmailVerified: true,
    creationTime: '2025-06-09T00:00:00Z',
    lastLoginTime: '2026-06-10T09:48:00Z',
    // 管理员锁定：无期限，直到解锁
    lockoutEnd: null,
    roles: ['Member'],
  },
  {
    id: 'user_yiming',
    username: 'yiming',
    email: 'yiming.wang@example.com',
    password: 'Yiming@123456',
    displayName: '王一鸣',
    isActive: false,
    isSuperAdmin: false,
    isEmailVerified: true,
    creationTime: '2025-07-21T00:00:00Z',
    lastLoginTime: '2026-05-09T03:20:00Z',
    roles: ['Member'],
  },
  {
    id: 'user_mei',
    username: 'mei.li',
    email: 'mei.li@example.net',
    password: 'Meili@123456',
    displayName: '李梅',
    isActive: true,
    isSuperAdmin: false,
    isEmailVerified: false,
    creationTime: '2025-09-30T00:00:00Z',
    roles: ['Member'],
  },
  //#if (ExternalLogin)
  // 只经外部登录建立、没有本地口令、已开两步验证的账号（Mock 下从 Google 登录进来）：
  // 演示"设置密码"、只能出示验证码的确认身份与只填验证码的停用两步验证
  {
    id: 'user_octo',
    username: 'octo',
    email: 'octo@example.com',
    displayName: 'Octo',
    isActive: true,
    isSuperAdmin: false,
    isEmailVerified: true,
    creationTime: '2025-10-08T00:00:00Z',
    lastLoginTime: '2026-06-11T07:30:00Z',
    twoFactorEnabled: true,
    recoveryCodes: Array.from(
      { length: 8 },
      (_, i) => `mock-${String(i).padStart(4, '0')}-code-demo`,
    ),
    roles: ['Member'],
  },
  //#endif
];
//#if (LocalIdentity)

/** Mock 下身份验证器上的验证码固定为这一个：登录第二步、确认身份与两步验证设置共用。 */
export const MOCK_TWO_FACTOR_CODE = '123456';

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

/** 凭据变了：递增安全版本，与后端轮换 SecurityStamp 同义（同值改口令也算一次）。 */
export function rotateSecurityVersion(user: MockUser): void {
  user.securityVersion = (user.securityVersion ?? 0) + 1;
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
    //#if (LocalIdentity)
    isTwoFactorEnabled: user.twoFactorEnabled === true,
    hasPassword: !!user.password,
    //#endif
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
