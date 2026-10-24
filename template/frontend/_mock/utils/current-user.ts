//#if (LocalIdentity)
import { fromBase64Url, toBase64Url } from '../../src/app/core/webauthn/webauthn';
//#endif
//#if (LocalIdentity || !IncludeMultiTenancy)
import { MockException } from '../core/models';
//#endif
//#if (LocalIdentity)
import {
  MOCK_PASSKEYS_AVAILABLE,
  MOCK_TWO_FACTOR_CODE,
  MockPasskey,
  MockUser,
  USERS,
} from '../data/user';
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

/** 确认身份时出示的凭证，一次只能一种；各接口的字段名不同，由调用处换成这三个名字。 */
export interface MockReauthenticationProof {
  password?: string;
  twoFactorCode?: string;
  /** 通行密钥断言（浏览器交回的凭据 JSON），先经 `me/reauthentication/passkey-options` 取选项。 */
  passkey?: unknown;
}

/** 仪式的有效期，与后端一致：5 分钟。 */
const MOCK_PASSKEY_CEREMONY_MS = 5 * 60_000;

interface MockPasskeyCeremony {
  challenge: string;
  expiresAt: number;
}

/**
 * 进行中的通行密钥仪式（挑战），按"用途:本人"或免口令登录的仪式凭据存放；取出即作废，只认一次。
 * 与其他业务数据一样只在本页内存里。
 */
const passkeyCeremonies = new Map<string, MockPasskeyCeremony>();

/** 生成一个新挑战并记下；同一键再取一次即替换，之前给的不再被认。 */
export function beginMockPasskeyCeremony(key: string): string {
  const challenge = toBase64Url(crypto.getRandomValues(new Uint8Array(32)));
  passkeyCeremonies.set(key, { challenge, expiresAt: Date.now() + MOCK_PASSKEY_CEREMONY_MS });
  return challenge;
}

/** 取出仪式的挑战并作废；没有或已过期时为 null。 */
export function takeMockPasskeyCeremony(key: string): string | null {
  const ceremony = passkeyCeremonies.get(key);
  passkeyCeremonies.delete(key);
  return ceremony && ceremony.expiresAt > Date.now() ? ceremony.challenge : null;
}

/** 断言选项（`PublicKeyCredentialRequestOptions` 的 JSON 形式）；不给 `rpId` 即取当前页面的域名。 */
export function mockPasskeyRequestOptions(
  challenge: string,
  allowed: readonly MockPasskey[] = [],
): Record<string, unknown> {
  return {
    challenge,
    timeout: MOCK_PASSKEY_CEREMONY_MS,
    userVerification: 'required',
    allowCredentials: allowed.map((passkey) => ({ type: 'public-key', id: passkey.credentialId })),
  };
}

/** 浏览器交回的凭据里 Mock 要看的部分：凭据 Id 与客户端数据（类型、挑战）。 */
export interface MockWebAuthnClientData {
  credentialId: string;
  type: string;
  challenge: string;
}

/** 解出凭据 Id 与 `clientDataJSON`；形状不对时为 null。Mock 不校验签名与证明。 */
export function readMockWebAuthnCredential(credential: unknown): MockWebAuthnClientData | null {
  const value = credential as
    { id?: unknown; response?: { clientDataJSON?: unknown } } | null | undefined;
  if (typeof value?.id !== 'string' || typeof value.response?.clientDataJSON !== 'string') {
    return null;
  }
  try {
    const clientData = JSON.parse(
      new TextDecoder().decode(fromBase64Url(value.response.clientDataJSON)),
    ) as { type?: unknown; challenge?: unknown };
    return typeof clientData.type === 'string' && typeof clientData.challenge === 'string'
      ? { credentialId: value.id, type: clientData.type, challenge: clientData.challenge }
      : null;
  } catch {
    return null;
  }
}

/**
 * 核对断言：客户端数据是这一次挑战的 `webauthn.get`，凭据 Id 已登记，且（给了 `owner` 时）属于这个人。
 * 认出来时记下最近使用并返回凭据与主人，认不出为 null。
 */
export function verifyMockPasskeyAssertion(
  credential: unknown,
  challenge: string,
  owner?: MockUser,
): { user: MockUser; passkey: MockPasskey } | null {
  const data = readMockWebAuthnCredential(credential);
  if (!data || data.type !== 'webauthn.get' || data.challenge !== challenge) {
    return null;
  }
  for (const user of owner ? [owner] : USERS) {
    const passkey = user.passkeys?.find((item) => item.credentialId === data.credentialId);
    if (passkey) {
      passkey.lastUsedTime = new Date().toISOString();
      return { user, passkey };
    }
  }
  return null;
}

export function passkeyCeremonyExpired(): MockException {
  return new MockException(400, {
    code: 'Auth:PasskeyCeremonyExpired',
    message: 'This passkey request has expired. Try again.',
  });
}

export function passkeyVerificationFailed(): MockException {
  return new MockException(400, {
    code: 'Auth:PasskeyVerificationFailed',
    message: "The passkey couldn't be verified. Try again.",
  });
}

/**
 * 复刻后端确认身份的可观察规则（见 `ReauthenticationGuard`）：
 * - 出示了不止一种凭证：400 字段错误，不带业务码；
 * - 出示了凭证就只按它判定，不回落到"刚认证过"：本账号不具备这种方式时
 *   `Auth:ReauthenticationMethodUnavailable`，口令或验证码不对时各自的错误码；通行密钥只认这次确认身份的挑战
 *   （没取过或已过期 `Auth:PasskeyCeremonyExpired`），凭据不是本人登记的 `Auth:PasskeyVerificationFailed`；
 * - 没有出示凭证：刚认证过即放行；否则口令、身份验证器与通行密钥都没有时 `Auth:SignInAgainRequired`，
 *   其余 `Auth:ReauthenticationRequired`。
 *
 * 管理员操作他人时传入的是操作者本人。
 */
export function ensureMockReauthentication(user: MockUser, proof: MockReauthenticationProof): void {
  const password = proof.password || undefined;
  const twoFactorCode = proof.twoFactorCode?.replace(/\s/g, '') || undefined;
  const passkey =
    typeof proof.passkey === 'object' && proof.passkey !== null ? proof.passkey : undefined;
  const presented = [password, twoFactorCode, passkey].filter(
    (value) => value !== undefined,
  ).length;

  if (presented > 1) {
    throw new MockException(400, {
      errors: [
        {
          field: 'twoFactorCode',
          detail: 'Provide either your current password or a verification code, not both.',
        },
      ],
    });
  }

  if (passkey !== undefined) {
    if (!MOCK_PASSKEYS_AVAILABLE || (user.passkeys?.length ?? 0) === 0) {
      throw new MockException(400, {
        code: 'Auth:ReauthenticationMethodUnavailable',
        message: 'This way of confirming your identity is not available for your account.',
      });
    }
    const challenge = takeMockPasskeyCeremony(`reauthentication:${user.id}`);
    if (!challenge) {
      throw passkeyCeremonyExpired();
    }
    if (!verifyMockPasskeyAssertion(passkey, challenge, user)) {
      throw passkeyVerificationFailed();
    }
    return;
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

  throw !user.password && user.twoFactorEnabled !== true && (user.passkeys?.length ?? 0) === 0
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
