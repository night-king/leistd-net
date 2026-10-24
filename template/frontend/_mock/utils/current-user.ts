//#if (LocalIdentity || !IncludeMultiTenancy)
import { MockException } from '../core/models';
//#endif
//#if (LocalIdentity)
import { MOCK_TWO_FACTOR_CODE, MockUser, USERS } from '../data/user';
//#else
import { MockUser, USERS } from '../data/user';
//#endif

// Mock 会话状态存在当前页面的 sessionStorage，刷新保持登录；用户数据同样存本页（见 data/user.ts），其余业务数据在页面内存里，重新加载即回到初始数据。
// 新页面的初始存储取决于浏览器：独立新开的页面为空（未登录），保留 opener 打开的页面会复制来源页的存储，
// 此后各页的会话互不影响（见 docs/standards/coding-frontend.md「Mock」）。每次都读存储、不在模块里缓存，存储是唯一来源。
const MOCK_SESSION_STORAGE_KEY = 'mock_session_user_id';
const MOCK_SESSION_TENANT_STORAGE_KEY = 'mock_session_tenant_key';
const MOCK_SESSION_SUBJECT_STORAGE_KEY = 'mock_session_subject_id';
/** 会话建立（登录）的时刻，对应真实会话的认证时间；确认身份按它判断"刚认证过"。 */
const MOCK_SESSION_AUTH_TIME_STORAGE_KEY = 'mock_session_auth_time';
//#if (Impersonation)
const MOCK_SESSION_IMPERSONATOR_STORAGE_KEY = 'mock_session_impersonator';
//#endif

export function setMockSessionUserId(id: string | null) {
  if (id) {
    sessionStorage.setItem(MOCK_SESSION_STORAGE_KEY, id);
    sessionStorage.setItem(MOCK_SESSION_AUTH_TIME_STORAGE_KEY, String(Date.now()));
  } else {
    sessionStorage.removeItem(MOCK_SESSION_STORAGE_KEY);
    sessionStorage.removeItem(MOCK_SESSION_AUTH_TIME_STORAGE_KEY);
    sessionStorage.removeItem(MOCK_SESSION_TENANT_STORAGE_KEY);
    sessionStorage.removeItem(MOCK_SESSION_SUBJECT_STORAGE_KEY);
    //#if (Impersonation)
    sessionStorage.removeItem(MOCK_SESSION_IMPERSONATOR_STORAGE_KEY);
    //#endif
  }
}
//#if (Impersonation)

/** 模拟登录的发起人，对应真实会话里的发起人声明；随会话一起保存、退出登录时一起清掉。 */
export interface MockImpersonator {
  userId: string;
  name: string;
  /** 发起人所在的租户键，结束模拟时回到这里。 */
  tenantKey: string;
}

export function getMockImpersonator(): MockImpersonator | null {
  const value = sessionStorage.getItem(MOCK_SESSION_IMPERSONATOR_STORAGE_KEY);
  return value ? (JSON.parse(value) as MockImpersonator) : null;
}

export function setMockImpersonator(impersonator: MockImpersonator | null): void {
  if (impersonator) {
    sessionStorage.setItem(MOCK_SESSION_IMPERSONATOR_STORAGE_KEY, JSON.stringify(impersonator));
  } else {
    sessionStorage.removeItem(MOCK_SESSION_IMPERSONATOR_STORAGE_KEY);
  }
}
//#endif

/**
 * 主体标识：按用户隔离数据时用它，而不是 Mock persona 的 id（Resource 形态下两者不同）。没有显式主体
 * 的会话（本地密码、外部登录、模拟登录）以会话用户为主体，同样每次读存储：本页登录、退出或切换账号后立即生效。
 */
export function getMockSessionSubjectId(): string | null {
  return sessionStorage.getItem(MOCK_SESSION_SUBJECT_STORAGE_KEY) ?? getMockSessionUserId();
}

/**
 * 登录时把租户定案进会话：真实后端优先使用已认证主体的租户声明
 * （CurrentPrincipalTenantResolveContributor），租户提示头只在匿名阶段起作用。
 */
export function setMockSessionTenantKey(tenantKey: string | null) {
  //#if (!IncludeMultiTenancy)
  if (tenantKey && tenantKey !== 'host') {
    throw new MockException(401, {
      code: 'Tenant:InvalidScope',
      message: 'A host session is required',
    });
  }
  //#endif
  if (tenantKey) {
    sessionStorage.setItem(MOCK_SESSION_TENANT_STORAGE_KEY, tenantKey);
  } else {
    sessionStorage.removeItem(MOCK_SESSION_TENANT_STORAGE_KEY);
  }
}

/** 当前会话的租户键；宿主上下文为 `host`。 */
export function getMockSessionTenantKey(): string {
  const tenantKey = sessionStorage.getItem(MOCK_SESSION_TENANT_STORAGE_KEY) ?? 'host';
  //#if (!IncludeMultiTenancy)
  if (tenantKey !== 'host') {
    throw new MockException(401, {
      code: 'Tenant:InvalidScope',
      message: 'A host session is required',
    });
  }
  //#endif
  return tenantKey;
}

/** 当前会话用户（persona）的 id；未登录时为 null。 */
export function getMockSessionUserId(): string | null {
  return sessionStorage.getItem(MOCK_SESSION_STORAGE_KEY);
}

/** 返回当前会话用户；未登录时返回 null（不抛异常，供权限接口自行决定 401/403）。 */
export function getCurrentUser(): MockUser | null {
  // 以存储为准：本页登录、退出或切换账号后，这里读到的就是新会话
  const userId = getMockSessionUserId();
  if (userId) getMockSessionTenantKey();
  return userId ? (USERS.find((item) => item.id === userId) ?? null) : null;
}
/** 为 Mock 会话记录主体与展示 persona，二者可以不同。 */
export function setMockSessionIdentity(
  subjectId: string,
  personaUserId: string,
  tenantId: string | null,
): void {
  // 先校验作用域，再写入已认证主体与 persona。
  setMockSessionTenantKey(tenantId);
  setMockSessionUserId(personaUserId);
  sessionStorage.setItem(MOCK_SESSION_SUBJECT_STORAGE_KEY, subjectId);
}
//#if (LocalIdentity)

/** 与后端一致：认证后 15 分钟内算"刚认证过"，敏感操作不必再出示凭证。 */
const MOCK_RECENT_AUTHENTICATION_MS = 15 * 60_000;

/** 会话认证时间往前推，让下一次敏感操作要求确认身份（演示与测试用）；未登录时什么也不做。 */
export function expireMockSessionAuthentication(): void {
  if (getMockSessionUserId()) {
    sessionStorage.setItem(MOCK_SESSION_AUTH_TIME_STORAGE_KEY, '0');
  }
}

/** 确认身份时出示的凭证，一次只能一种；各接口的字段名不同，由调用处换成这两个名字。 */
export interface MockReauthenticationProof {
  password?: string;
  twoFactorCode?: string;
}

/**
 * 复刻后端确认身份的可观察规则（见 `ReauthenticationGuard`）：
 * - 两种凭证同时出示：400 字段错误，不带业务码；
 * - 出示了凭证就只按它判定，不回落到"刚认证过"：本账号不具备这种方式时
 *   `Auth:ReauthenticationMethodUnavailable`，口令或验证码不对时各自的错误码；
 * - 没有出示凭证：刚认证过即放行；否则既无口令又没开两步验证时 `Auth:SignInAgainRequired`，
 *   其余 `Auth:ReauthenticationRequired`。
 *
 * 管理员操作他人时传入的是操作者本人。
 */
export function ensureMockReauthentication(user: MockUser, proof: MockReauthenticationProof): void {
  const password = proof.password || undefined;
  const twoFactorCode = proof.twoFactorCode?.replace(/\s/g, '') || undefined;

  if (password !== undefined && twoFactorCode !== undefined) {
    throw new MockException(400, {
      errors: [
        {
          field: 'twoFactorCode',
          detail: 'Provide either your current password or a verification code, not both.',
        },
      ],
    });
  }

  if (password !== undefined || twoFactorCode !== undefined) {
    const available = password !== undefined ? !!user.password : user.twoFactorEnabled === true;
    if (!available) {
      throw new MockException(400, {
        code: 'Auth:ReauthenticationMethodUnavailable',
        message: 'This way of confirming your identity is not available for your account.',
      });
    }
    if (password !== undefined && password !== user.password) {
      throw new MockException(400, {
        code: 'Security:CurrentPasswordIncorrect',
        message: 'The current password is incorrect.',
      });
    }
    if (twoFactorCode !== undefined && twoFactorCode !== MOCK_TWO_FACTOR_CODE) {
      throw new MockException(400, {
        code: 'Auth:TwoFactorCodeInvalid',
        message: `The verification code is incorrect (mock code: ${MOCK_TWO_FACTOR_CODE}).`,
      });
    }
    return;
  }

  const authenticatedAt = Number(sessionStorage.getItem(MOCK_SESSION_AUTH_TIME_STORAGE_KEY) ?? 0);
  if (Date.now() - authenticatedAt < MOCK_RECENT_AUTHENTICATION_MS) {
    return;
  }

  throw !user.password && user.twoFactorEnabled !== true
    ? new MockException(400, {
        code: 'Auth:SignInAgainRequired',
        message: 'Sign in again to continue.',
      })
    : new MockException(400, {
        code: 'Auth:ReauthenticationRequired',
        message: 'Confirm your identity to continue.',
      });
}
//#endif
