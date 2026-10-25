import { ROLES } from './authorization';
import { UserManagementOutputDto } from '../../src/app/features/platform/dtos/user-management.dto';
import { UserOutputDto } from '../../src/app/shared/dtos/auth.dto';

//#if (LocalIdentity)
/** 一把已登记的通行密钥：Mock 只记浏览器交回的凭据 Id 与展示字段，不存公钥、不校验签名。 */
export interface MockPasskey {
  id: string;
  /** 浏览器交回的凭据 Id（base64url），验证时只认它。 */
  credentialId: string;
  name: string;
  creationTime: string;
  lastUsedTime?: string | null;
  isBackupEligible: boolean;
  isBackedUp: boolean;
}

//#endif
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
  /** 登记的通行密钥。 */
  passkeys?: MockPasskey[];
  /** 尚未用过的恢复码，个数即"剩余恢复码"；挂在账号上，有任一独立因子（身份验证器或通行密钥）才有。 */
  recoveryCodes?: string[];
  /** 开启了邮箱验证码作为登录第二步（能否使用还看邮箱已验证与组织开关）。 */
  emailTwoFactorEnabled?: boolean;
  /** 本人指定的首选第二步方式。 */
  preferredTwoFactorMethod?: 'passkey' | 'authenticator' | 'email';
  /** 当前口令经邮件找回设置、还没用独立因子完成过第二步：登录第二步只认身份验证器与恢复码。 */
  passwordSetByEmailRecovery?: boolean;
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

/** Mock 部署视为配置了通行密钥的依赖方（RP ID 取当前页面的域名）。 */
export const MOCK_PASSKEYS_AVAILABLE = true;

/** 有独立因子（身份验证器或至少一把通行密钥）：满足组织"要求两步验证"，并持有恢复码。 */
export function hasIndependentFactor(user: MockUser): boolean {
  return user.twoFactorEnabled === true || (user.passkeys?.length ?? 0) > 0;
}

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

// 用户与设置一样存本页 sessionStorage：Mock 启动时恢复（provideMock），每个请求处理完写回（Mock 拦截器）。
// 否则刷新或整页跳转（退出登录）后用户回到初始数据，而设置还在——例如开着"要求两步验证"却忘了已绑定的验证器。
// 单测直接调处理器、不经 provideMock，不读这份存储，结果不随文件执行顺序变化。
const USERS_STORAGE_KEY = 'mock_users';
//#if (LocalIdentity)
const DELETED_USERS_STORAGE_KEY = 'mock_deleted_users';
//#endif

/** 用本页 sessionStorage 里上次的用户数据替换初始数据；没有存过时保持初始数据。 */
export function restoreMockUsers(): void {
  restoreInto(USERS, USERS_STORAGE_KEY);
  //#if (LocalIdentity)
  restoreInto(DELETED_USERS, DELETED_USERS_STORAGE_KEY);
  //#endif
}

/** 把当前用户数据写回本页 sessionStorage。 */
export function persistMockUsers(): void {
  sessionStorage.setItem(USERS_STORAGE_KEY, JSON.stringify(USERS));
  //#if (LocalIdentity)
  sessionStorage.setItem(DELETED_USERS_STORAGE_KEY, JSON.stringify(DELETED_USERS));
  //#endif
}

function restoreInto(target: MockUser[], storageKey: string): void {
  const raw = sessionStorage.getItem(storageKey);
  if (raw) {
    target.splice(0, target.length, ...(JSON.parse(raw) as MockUser[]));
  }
}

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
    hasPasskeys: (user.passkeys?.length ?? 0) > 0,
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
    // 与后端一致：任一种第二步方式（身份验证器、通行密钥或邮箱验证码）都算，有就能重置
    isTwoFactorEnabled:
      user.twoFactorEnabled === true ||
      (user.passkeys?.length ?? 0) > 0 ||
      user.emailTwoFactorEnabled === true,
    //#endif
  };
}
