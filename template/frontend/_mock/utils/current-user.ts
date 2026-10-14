//#if (!IncludeMultiTenancy)
import { MockException } from '../core/models';
//#endif
import { MockUser, USERS } from '../data/user';

// Mock 会话状态存在当前页面的 sessionStorage，刷新保持登录；业务数据在页面内存里，重新加载即回到初始数据。
// 新页面的初始存储取决于浏览器：独立新开的页面为空（未登录），保留 opener 打开的页面会复制来源页的存储，
// 此后各页的会话互不影响（见 docs/standards/coding-frontend.md「Mock」）。每次都读存储、不在模块里缓存，存储是唯一来源。
const MOCK_SESSION_STORAGE_KEY = 'mock_session_user_id';
const MOCK_SESSION_TENANT_STORAGE_KEY = 'mock_session_tenant_key';
const MOCK_SESSION_SUBJECT_STORAGE_KEY = 'mock_session_subject_id';
//#if (Impersonation)
const MOCK_SESSION_IMPERSONATOR_STORAGE_KEY = 'mock_session_impersonator';
//#endif

export function setMockSessionUserId(id: string | null) {
  if (id) {
    sessionStorage.setItem(MOCK_SESSION_STORAGE_KEY, id);
  } else {
    sessionStorage.removeItem(MOCK_SESSION_STORAGE_KEY);
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
