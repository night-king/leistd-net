//#if (Impersonation)
import { ImpersonationStatus } from '../../src/app/core/services/impersonation-service';
//#endif
//#if (IncludeMultiTenancy)
import { TENANT_HEADER } from '../../src/app/core/tenancy/tenant-protocol';
//#endif
import {
  ChangePasswordInputDto,
  UpdateCurrentUserInputDto,
  RegisterInputDto,
  CaptchaOutputDto,
  //#if (Email)
  SendEmailCodeInputDto,
  //#endif
  //#if (Email)
  EmailVerificationChallengeOutputDto,
  //#endif
  //#if (Email)
  EmailVerificationInputDto,
  //#endif
  SetAvatarInputDto,
  TwoFactorLoginInputDto,
  TwoFactorRecoveryCodesOutputDto,
  TwoFactorSetupOutputDto,
  //#if (OpenIddictServer)
  LogoutConfirmationOutputDto,
  //#endif
} from '../../src/app/features/account/dtos/account.dto';
import {
  SecurityConfigOutputDto,
  TwoFactorStatusOutputDto,
  UserSessionOutputDto,
} from '../../src/app/shared/dtos/account-security.dto';
import { SessionLoginOutputDto, UserOutputDto } from '../../src/app/shared/dtos/auth.dto';
import { MockException, MockRequest } from '../core/models';
import { ensureAcceptablePassword } from '../data/password-policy';
//#if (ExternalLogin)
import { availableExternalProviders, TENANT_SETTING_VALUES } from '../data/settings';
//#else
import { TENANT_SETTING_VALUES } from '../data/settings';
//#endif
//#if (IncludeMultiTenancy)
import { TENANTS } from '../data/tenant';
//#endif
import { MockUser, USERS, isEmailTaken, isUsernameTaken, toUserOutput } from '../data/user';
import {
  getCurrentUser as getSessionMockUser,
  //#if (Impersonation)
  getMockImpersonator,
  //#endif
  getMockSessionTenantKey,
  //#if (Impersonation)
  setMockImpersonator,
  //#endif
  setMockSessionTenantKey,
  setMockSessionUserId,
} from '../utils/current-user';

const CAPTCHA_LETTERS = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz';
const CAPTCHA_DIGITS = '23456789';
const CAPTCHA_CHARACTERS = CAPTCHA_LETTERS + CAPTCHA_DIGITS;
const captchaStore = new Map<string, string>();
//#if (Email)
const EMAIL_VERIFICATION_ENABLED = false;
const EMAIL_CODE = '123456';
const EMAIL_CODE_EXPIRY_SECONDS = 300;
const EMAIL_CODE_RETRY_SECONDS = 60;
const EMAIL_CODE_MAX_ATTEMPTS = 5;

interface EmailVerificationChallenge {
  scope: string;
  purpose: 'registration-email' | 'account-email';
  email: string;
  code: string;
  expiresAt: number;
  remainingAttempts: number;
}

const emailChallengeStore = new Map<string, EmailVerificationChallenge>();
const emailRateLimitStore = new Map<string, number>();

//#endif
function ensureUsernameAvailable(username: string, currentUserId?: string): void {
  if (isUsernameTaken(username, currentUserId)) {
    throw new MockException(409, {
      code: 'User:UsernameTaken',
      message: 'Username already exists',
    });
  }
}

function ensureEmailAvailable(email: string, currentUserId?: string): void {
  if (isEmailTaken(email, currentUserId)) {
    throw new MockException(409, { code: 'User:EmailTaken', message: 'Email is already in use' });
  }
}
//#if (IncludeMultiTenancy)

/** 复刻后端行为：租户提示头指向已停用租户时登录被 403 拒绝。 */
function ensureTenantActive(req: MockRequest): void {
  const tenantId = req.headers.get(TENANT_HEADER);
  if (!tenantId) {
    return;
  }
  const tenant = TENANTS.find((t) => t.id === tenantId);
  if (tenant && !tenant.isActive) {
    throw new MockException(403, { code: 'Tenant:NotActive', message: 'Tenant is deactivated' });
  }
}
//#endif

function sessionLogin(
  usernameOrEmail: string,
  password: string,
  tenantKey: string,
): SessionLoginOutputDto {
  const user = USERS.find((u) => u.username === usernameOrEmail || u.email === usernameOrEmail);

  if (user && user.password === password) {
    // 与后端一致：启用了两步验证时密码对了也不下发会话，只给第二步凭据
    if (user.twoFactorEnabled) {
      return beginTwoFactorChallenge(user, tenantKey);
    }
    setMockSessionUserId(user.id);
    // 租户在登录这一刻定案，之后由会话（真实环境是 cookie 里的租户声明）说话；
    // 认证后的接口不再看租户提示头，与后端的解析链一致。
    setMockSessionTenantKey(tenantKey);
    return {};
  }

  throw new MockException(401, {
    code: 'Auth:InvalidCredentials',
    message: 'Incorrect username or password',
  });
}

/**
 * 当前认证主体，受保护端点的唯一入口。没有会话或匹配不到 Mock 用户一律 401，不回落到 USERS[0]：
 * 否则匿名的资料修改与改密码会"成功"，与真后端不一致。persona 回落只用于 Resource 形态的权限演示。
 */
function requireCurrentMockUser(): MockUser {
  const user = getSessionMockUser();
  if (!user) {
    throw new MockException(401, { message: 'Not authenticated' });
  }
  return user;
}

function getCurrentUser(_req: MockRequest): UserOutputDto {
  return toUserOutput(requireCurrentMockUser());
}

function updateCurrentUser(req: MockRequest): UserOutputDto {
  // 先确立主体再读 body：匿名写入不得在失败前碰到任何用户数据
  const user = requireCurrentMockUser();
  const body = req.body as UpdateCurrentUserInputDto;

  const username = body.username.trim();
  const email = body.email.trim();

  if (!username) {
    throw new MockException(400, { message: 'Username is required' });
  }

  if (!email) {
    throw new MockException(400, { message: 'Email is required' });
  }

  ensureUsernameAvailable(username, user.id);
  ensureEmailAvailable(email, user.id);

  // 换了邮箱即回到未验证，与后端 User.UpdateProfile 同一规则
  if (normalizeEmail(user.email) !== normalizeEmail(email)) {
    user.isEmailVerified = false;
  }

  user.username = username;
  user.email = email;
  user.displayName = body.displayName?.trim() || undefined;
  user.phoneNumber = body.phoneNumber?.trim() || undefined;

  return toUserOutput(user);
}

/** 与后端 `SetAvatarInputDto` 同一判据：本人入口只收图片 data URL，空值表示清除。 */
const AVATAR_DATA_URL = /^data:image\/(png|jpeg|webp);base64,[A-Za-z0-9+/=]*$/;

function setCurrentUserAvatar(req: MockRequest): UserOutputDto {
  const user = requireCurrentMockUser();
  const body = req.body as SetAvatarInputDto;
  // 入参校验：外部地址等非图片 data URL 是 400 字段错误、不带业务码
  if (body.avatar && !AVATAR_DATA_URL.test(body.avatar)) {
    throw new MockException(400, {
      errors: [{ field: 'avatar', detail: 'Avatar must be a PNG, JPEG or WebP image.' }],
    });
  }
  user.avatar = body.avatar || undefined;
  return toUserOutput(user);
}
//#if (Email)

function sendCurrentUserEmailCode(req: MockRequest): EmailVerificationChallengeOutputDto {
  const user = requireCurrentMockUser();
  if (user.isEmailVerified) {
    throw new MockException(409, {
      code: 'Auth:EmailAlreadyVerified',
      message: 'This email address has already been verified.',
    });
  }

  const challengeId = crypto.randomUUID();
  emailChallengeStore.set(challengeId, {
    scope: getRequestScope(req),
    purpose: 'account-email',
    email: normalizeEmail(user.email),
    code: EMAIL_CODE,
    expiresAt: Date.now() + EMAIL_CODE_EXPIRY_SECONDS * 1000,
    remainingAttempts: EMAIL_CODE_MAX_ATTEMPTS,
  });

  return {
    challengeId,
    expiresInSeconds: EMAIL_CODE_EXPIRY_SECONDS,
    retryAfterSeconds: EMAIL_CODE_RETRY_SECONDS,
  };
}
//#endif
//#if (Email)

function confirmCurrentUserEmail(req: MockRequest): UserOutputDto {
  const user = requireCurrentMockUser();
  const body = req.body as EmailVerificationInputDto;
  const challenge = emailChallengeStore.get(body.challengeId);
  if (
    !challenge ||
    challenge.purpose !== 'account-email' ||
    challenge.email !== normalizeEmail(user.email) ||
    challenge.code !== body.code.trim()
  ) {
    throw new MockException(400, {
      code: 'Auth:EmailCodeInvalid',
      message: 'The email verification code is incorrect or has expired.',
    });
  }

  emailChallengeStore.delete(body.challengeId);
  user.isEmailVerified = true;
  return toUserOutput(user);
}
//#endif

function changePassword(req: MockRequest): 'ok' {
  const user = requireCurrentMockUser();
  const body = req.body as ChangePasswordInputDto;

  if (user.password !== body.currentPassword) {
    throw new MockException(400, {
      code: 'Security:CurrentPasswordIncorrect',
      message: 'Current password is incorrect',
    });
  }

  if (body.newPassword !== body.confirmPassword) {
    throw new MockException(400, {
      message: 'The new passwords do not match',
    });
  }

  if (body.currentPassword === body.newPassword) {
    throw new MockException(400, {
      message: 'The new password must be different from the current password',
    });
  }

  ensureAcceptablePassword(body.newPassword, 'New password');

  user.password = body.newPassword;
  // 与服务端一致：改密码后其他设备退出登录
  mockSessions = mockSessions.filter((s) => s.isCurrent);
  return 'ok';
}

/**
 * 当前会话之外预置两台"别的设备"，让登录设备一节在 mock 下有内容可看、可撤销。
 * 模块级状态：撤销后在本次页面生命周期内保持，刷新即复原。
 */
const MOCK_SESSION_NOW = Date.now();
let mockSessions: UserSessionOutputDto[] = [
  {
    id: 'mock-session-current',
    creationTime: new Date(MOCK_SESSION_NOW - 2 * 3600_000).toISOString(),
    lastSeenTime: new Date(MOCK_SESSION_NOW).toISOString(),
    ipAddress: '127.0.0.1',
    userAgent: typeof navigator === 'undefined' ? null : navigator.userAgent,
    isCurrent: true,
  },
  {
    id: 'mock-session-phone',
    creationTime: new Date(MOCK_SESSION_NOW - 3 * 86400_000).toISOString(),
    lastSeenTime: new Date(MOCK_SESSION_NOW - 5 * 3600_000).toISOString(),
    ipAddress: '203.0.113.24',
    userAgent:
      'Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1',
    isCurrent: false,
  },
  {
    id: 'mock-session-laptop',
    creationTime: new Date(MOCK_SESSION_NOW - 6 * 86400_000).toISOString(),
    lastSeenTime: new Date(MOCK_SESSION_NOW - 26 * 3600_000).toISOString(),
    ipAddress: '198.51.100.7',
    userAgent:
      'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36 Edg/128.0.2739.42',
    isCurrent: false,
  },
];

/**
 * 两步验证：mock 下验证码固定为 {@link MOCK_TWO_FACTOR_CODE}，启用状态与恢复码记在各 Mock 用户上，
 * 因此登录第二步与管理员重置都能演示。
 */
const MOCK_TWO_FACTOR_CODE = '123456';
/** 与后端单个挑战的尝试上限同一量级：输错到上限即作废，只能回到第一步。 */
const TWO_FACTOR_CHALLENGE_MAX_ATTEMPTS = 5;

interface TwoFactorChallenge {
  userId: string;
  tenantKey: string;
  remainingAttempts: number;
}

const twoFactorChallengeStore = new Map<string, TwoFactorChallenge>();

function beginTwoFactorChallenge(user: MockUser, tenantKey: string): SessionLoginOutputDto {
  const twoFactorToken = crypto.randomUUID();
  twoFactorChallengeStore.set(twoFactorToken, {
    userId: user.id,
    tenantKey,
    remainingAttempts: TWO_FACTOR_CHALLENGE_MAX_ATTEMPTS,
  });
  return { requiresTwoFactor: true, twoFactorToken };
}

function twoFactorChallengeExpired(): MockException {
  return new MockException(401, {
    code: 'Auth:TwoFactorChallengeExpired',
    message: 'The sign-in attempt has expired. Sign in again.',
  });
}

/**
 * 登录第二步。入参校验与后端 `TwoFactorLoginInputDto` 一致（400 字段错误、不带业务码）；
 * 凭据不存在、不属于当前租户或已用尽尝试次数一律 401，验证码错误 400。
 */
function completeTwoFactorLogin(req: MockRequest): 'ok' {
  const body = (req.body ?? {}) as TwoFactorLoginInputDto;
  const errors = [];
  if (!body.token?.trim()) {
    errors.push({ field: 'token', detail: 'Two-factor token is required.' });
  }
  if (!body.code?.trim() && !body.recoveryCode?.trim()) {
    errors.push({ field: 'code', detail: 'Enter the verification code or a recovery code.' });
  }
  if (errors.length > 0) {
    throw new MockException(400, { errors });
  }

  const challenge = twoFactorChallengeStore.get(body.token);
  const user = challenge ? USERS.find((u) => u.id === challenge.userId) : undefined;
  if (!challenge || !user || challenge.tenantKey !== getRequestScope(req)) {
    throw twoFactorChallengeExpired();
  }

  // 都给了时以恢复码为准，与后端同一取舍；恢复码用一次即作废
  const recoveryCode = body.recoveryCode?.trim();
  const verified = recoveryCode
    ? (user.recoveryCodes ?? []).includes(recoveryCode)
    : body.code?.replace(/\s/g, '') === MOCK_TWO_FACTOR_CODE;
  if (!verified) {
    challenge.remainingAttempts--;
    if (challenge.remainingAttempts <= 0) {
      twoFactorChallengeStore.delete(body.token);
      throw twoFactorChallengeExpired();
    }
    throw new MockException(400, {
      code: 'Auth:TwoFactorCodeInvalid',
      message: `The verification code is incorrect (mock code: ${MOCK_TWO_FACTOR_CODE}).`,
    });
  }

  if (recoveryCode) {
    user.recoveryCodes = (user.recoveryCodes ?? []).filter((code) => code !== recoveryCode);
  }
  twoFactorChallengeStore.delete(body.token);
  setMockSessionUserId(user.id);
  setMockSessionTenantKey(challenge.tenantKey);
  return 'ok';
}

function issueRecoveryCodes(user: MockUser): TwoFactorRecoveryCodesOutputDto {
  const codes = Array.from(
    { length: 10 },
    (_, i) => `mock-${String(i).padStart(4, '0')}-code-demo`,
  );
  user.twoFactorEnabled = true;
  user.recoveryCodes = [...codes];
  return { recoveryCodes: codes };
}

function ensureMockTwoFactorCode(code: string | undefined): void {
  if (code !== MOCK_TWO_FACTOR_CODE) {
    throw new MockException(400, {
      code: 'Auth:TwoFactorCodeInvalid',
      message: `The verification code is incorrect (mock code: ${MOCK_TWO_FACTOR_CODE}).`,
    });
  }
}

function getTwoFactorStatus(): TwoFactorStatusOutputDto {
  const user = requireCurrentMockUser();
  return {
    enabled: user.twoFactorEnabled === true,
    recoveryCodesLeft: user.recoveryCodes?.length ?? 0,
    requiredByPolicy: false,
  };
}

/** 最近一次开始的设置：只认它，别处重新开始过就作废（同后端）。Mock 的会话总算"刚认证过"，不要口令。 */
let mockPendingSetupId: string | null = null;

function beginTwoFactorSetup(req: MockRequest): TwoFactorSetupOutputDto {
  const user = requireCurrentMockUser();
  if (user.twoFactorEnabled) {
    // 换绑：出示现有验证器上的码，或者用掉一个恢复码
    const recoveryCode = req.body?.recoveryCode as string | undefined;
    if (recoveryCode) {
      const index = (user.recoveryCodes ?? []).indexOf(recoveryCode.trim());
      if (index < 0) {
        ensureMockTwoFactorCode(undefined);
      }
      user.recoveryCodes = (user.recoveryCodes ?? []).filter((_, i) => i !== index);
    } else if (req.body?.code) {
      ensureMockTwoFactorCode(req.body.code);
    } else {
      throw new MockException(409, {
        code: 'Auth:TwoFactorAlreadyEnabled',
        message: 'Two-factor authentication is already turned on.',
      });
    }
  }

  const secret = 'JBSWY3DPEHPK3PXP';
  mockPendingSetupId = crypto.randomUUID();
  return {
    setupId: mockPendingSetupId,
    secret,
    otpAuthUri: `otpauth://totp/Mock:${encodeURIComponent(user.username)}?secret=${secret}&issuer=Mock`,
  };
}

function enableTwoFactor(req: MockRequest): TwoFactorRecoveryCodesOutputDto {
  const user = requireCurrentMockUser();
  if (!mockPendingSetupId || req.body?.setupId !== mockPendingSetupId) {
    throw new MockException(400, {
      code: 'Auth:TwoFactorSetupExpired',
      message: 'The setup is no longer valid. Start again.',
    });
  }
  ensureMockTwoFactorCode(req.body?.code);
  mockPendingSetupId = null;
  mockSessions = mockSessions.filter((s) => s.isCurrent);
  return issueRecoveryCodes(user);
}

function disableTwoFactor(req: MockRequest): 'ok' {
  const user = requireCurrentMockUser();
  if (req.body?.password !== user.password) {
    throw new MockException(400, {
      code: 'Security:CurrentPasswordIncorrect',
      message: 'The current password is incorrect.',
    });
  }
  ensureMockTwoFactorCode(req.body?.code);
  user.twoFactorEnabled = false;
  user.recoveryCodes = [];
  mockSessions = mockSessions.filter((s) => s.isCurrent);
  return 'ok';
}

function regenerateRecoveryCodes(req: MockRequest): TwoFactorRecoveryCodesOutputDto {
  const user = requireCurrentMockUser();
  ensureMockTwoFactorCode(req.body?.code);
  return issueRecoveryCodes(user);
}

function getSessions(): UserSessionOutputDto[] {
  requireCurrentMockUser();
  return mockSessions;
}

function revokeSession(req: MockRequest): 'ok' {
  requireCurrentMockUser();
  const id = String(req.params.id);
  if (mockSessions.some((s) => s.id === id && s.isCurrent)) {
    throw new MockException(409, {
      code: 'Auth:CannotRevokeCurrentSession',
      message: 'Use sign-out to end the current session.',
    });
  }
  mockSessions = mockSessions.filter((s) => s.id !== id);
  return 'ok';
}

function revokeOtherSessions(): number {
  requireCurrentMockUser();
  const count = mockSessions.filter((s) => !s.isCurrent).length;
  mockSessions = mockSessions.filter((s) => s.isCurrent);
  return count;
}

function logout(): 'ok' {
  setMockSessionUserId(null);
  return 'ok';
}
//#if (Impersonation)

/** 模拟状态读会话里记下的发起人；不在模拟态时只回 `isImpersonating: false`。 */
function getImpersonationStatus(): ImpersonationStatus {
  requireCurrentMockUser();
  const impersonator = getMockImpersonator();
  if (!impersonator) {
    return { isImpersonating: false };
  }
  const tenant = TENANTS.find((t) => t.id === getMockSessionTenantKey());
  return {
    isImpersonating: true,
    impersonatorName: impersonator.name,
    tenantName: tenant?.displayName ?? tenant?.name,
  };
}

/** 结束模拟：会话切回发起人所在的宿主上下文。不在模拟态时 409，与后端同码。 */
function endImpersonation(): 'ok' {
  requireCurrentMockUser();
  const impersonator = getMockImpersonator();
  if (!impersonator) {
    throw new MockException(409, {
      code: 'Tenant:NotImpersonating',
      message: 'The current session is not impersonating.',
    });
  }
  if (!USERS.some((u) => u.id === impersonator.userId)) {
    throw new MockException(404, {
      code: 'User:NotFound',
      message: 'The impersonating user no longer exists.',
    });
  }
  setMockSessionUserId(impersonator.userId);
  setMockSessionTenantKey(impersonator.tenantKey);
  setMockImpersonator(null);
  return 'ok';
}
//#endif
//#if (OpenIddictServer)

function getLogoutConfirmation(req: MockRequest): LogoutConfirmationOutputDto {
  // Mock 没有协议端点：只要求确认页的两个引用都在，便于在开发态预览确认页
  return req.queryParams['request_uri'] && req.queryParams['confirmation']
    ? {
        isValid: true,
        applicationName: 'Mock application',
        antiforgeryFieldName: '__RequestVerificationToken',
        antiforgeryToken: 'mock-antiforgery-token',
      }
    : { isValid: false };
}
//#endif

/**
 * 请求所在租户是否开放自助注册：租户（或宿主）覆盖值，没有就是默认开放。与后端的租户解析一致，
 * 已登录时以会话的租户为准（提示头改写不了），匿名时才看提示头。
 */
function isRegistrationOpen(req: MockRequest): boolean {
  const scope = getSessionMockUser() ? getMockSessionTenantKey() : getRequestScope(req);
  return TENANT_SETTING_VALUES.get(`${scope}:Registration.Enabled`) !== 'false';
}

/** 与后端一致：关闭自助注册时注册与注册验证码都拒绝；外部登录首次登录不经过这里。 */
function ensureRegistrationOpen(req: MockRequest): void {
  if (!isRegistrationOpen(req)) {
    throw new MockException(403, {
      code: 'Auth:RegistrationDisabled',
      message: 'Self sign-up is not open. Ask an administrator for an account.',
    });
  }
}

function getSecurityConfig(req: MockRequest): SecurityConfigOutputDto {
  return {
    registrationEnabled: isRegistrationOpen(req),
    //#if (Email)
    enableEmailVerification: EMAIL_VERIFICATION_ENABLED,
    emailVerificationAvailable: true,
    //#endif
  };
}

function getCaptcha(): CaptchaOutputDto {
  const bgColors = ['#f0fdf4', '#f8fafc', '#fffbeb', '#fef2f2', '#f0f9ff'];
  const bg = bgColors[Math.floor(Math.random() * bgColors.length)];
  const lineY = Math.floor(Math.random() * 40);
  const angle = Math.floor(Math.random() * 20) - 10;
  const code = generateCaptchaCode(4);
  const token = Math.random().toString(36).substring(7);

  captchaStore.set(token, code);

  const svgContent = `<svg xmlns="http://www.w3.org/2000/svg" width="130" height="44"><rect width="100%" height="100%" fill="${bg}"/><line x1="0" y1="${lineY}" x2="130" y2="${44 - lineY}" stroke="#94a3b8" stroke-width="2" opacity="0.6"/><text x="50%" y="50%" font-size="24" font-family="monospace" fill="#0f172a" font-weight="bold" font-style="italic" textLength="88" lengthAdjust="spacingAndGlyphs" dominant-baseline="central" text-anchor="middle" transform="rotate(${angle}, 65, 22)">${code}</text></svg>`;

  const fakeImage = `data:image/svg+xml;base64,${btoa(svgContent)}`;

  return {
    captchaToken: token,
    captchaImageBase64: fakeImage,
  };
}

function generateCaptchaCode(length: number): string {
  const chars = [
    CAPTCHA_LETTERS[Math.floor(Math.random() * CAPTCHA_LETTERS.length)],
    CAPTCHA_DIGITS[Math.floor(Math.random() * CAPTCHA_DIGITS.length)],
    ...Array.from(
      { length: length - 2 },
      () => CAPTCHA_CHARACTERS[Math.floor(Math.random() * CAPTCHA_CHARACTERS.length)],
    ),
  ];

  for (let i = chars.length - 1; i > 0; i--) {
    const j = Math.floor(Math.random() * (i + 1));
    [chars[i], chars[j]] = [chars[j], chars[i]];
  }

  return chars.join('');
}

function validateCaptcha(captchaToken: string | undefined, captchaCode: string | undefined): void {
  const code = captchaToken ? captchaStore.get(captchaToken) : undefined;
  captchaStore.delete(captchaToken ?? '');

  if (!code || !captchaCode || code.toLowerCase() !== captchaCode.trim().toLowerCase()) {
    throw new MockException(400, {
      code: 'Auth:CaptchaInvalid',
      message: 'The captcha is incorrect, please try again',
    });
  }
}
//#if (Email)

function sendEmailCode(req: MockRequest): EmailVerificationChallengeOutputDto {
  ensureRegistrationOpen(req);
  const body = req.body as SendEmailCodeInputDto;
  validateCaptcha(body.captchaToken, body.captchaCode);

  // 占用判定与后端同一口径：按唯一索引原样比较，只差大小写的是另一个地址
  ensureEmailAvailable(body.email.trim());
  const email = normalizeEmail(body.email);
  const scope = getRequestScope(req);
  const rateKey = `${scope}:${email}`;
  const now = Date.now();
  if ((emailRateLimitStore.get(rateKey) ?? 0) > now) {
    throw new MockException(429, {
      code: 'Auth:EmailCodeSendTooFrequent',
      message: 'Verification codes are being sent too frequently',
    });
  }

  const challengeId = crypto.randomUUID();
  emailRateLimitStore.set(rateKey, now + EMAIL_CODE_RETRY_SECONDS * 1000);
  emailChallengeStore.set(challengeId, {
    scope,
    purpose: 'registration-email',
    email,
    code: EMAIL_CODE,
    expiresAt: now + EMAIL_CODE_EXPIRY_SECONDS * 1000,
    remainingAttempts: EMAIL_CODE_MAX_ATTEMPTS,
  });

  return {
    challengeId,
    expiresInSeconds: EMAIL_CODE_EXPIRY_SECONDS,
    retryAfterSeconds: EMAIL_CODE_RETRY_SECONDS,
  };
}
//#endif

function register(req: MockRequest): 'ok' {
  ensureRegistrationOpen(req);
  const body = req.body as RegisterInputDto;

  const username = body.username.trim();
  // 与后端一致：占用判定与保存都用原样地址（只差大小写的是另一个地址）；
  // 只有验证码按规范化地址归档
  const email = body.email.trim();

  ensureUsernameAvailable(username);
  ensureEmailAvailable(email);

  //#if (Email)
  if (EMAIL_VERIFICATION_ENABLED) {
    validateEmailChallenge(req, normalizeEmail(email), body);
  } else {
    validateCaptcha(body.captchaToken, body.captchaCode);
  }
  //#else
  validateCaptcha(body.captchaToken, body.captchaCode);
  //#endif

  ensureAcceptablePassword(body.password, 'Password');

  const newUser = {
    id: `user_${Date.now()}`,
    username: username,
    email: email,
    password: body.password,
    roles: ['User'],
    isActive: true,
    isSuperAdmin: false,
    isEmailVerified: false,
    creationTime: new Date().toISOString(),
  };
  USERS.push(newUser as any);

  // 注册完可按需设置登录态，这里选择不自动登录
  return 'ok';
}
//#if (Email)

function validateEmailChallenge(req: MockRequest, email: string, body: RegisterInputDto): void {
  const verification = body.emailVerification;
  const challenge = verification ? emailChallengeStore.get(verification.challengeId) : undefined;

  if (
    !verification ||
    !challenge ||
    challenge.scope !== getRequestScope(req) ||
    challenge.purpose !== 'registration-email' ||
    challenge.email !== email
  ) {
    throw invalidEmailChallenge();
  }

  if (challenge.expiresAt <= Date.now()) {
    emailChallengeStore.delete(verification.challengeId);
    throw invalidEmailChallenge();
  }

  if (challenge.code !== verification.code?.trim()) {
    challenge.remainingAttempts--;
    if (challenge.remainingAttempts <= 0) {
      emailChallengeStore.delete(verification.challengeId);
    }
    throw invalidEmailChallenge();
  }

  emailChallengeStore.delete(verification.challengeId);
}
//#endif
//#if (Email)

function invalidEmailChallenge(): MockException {
  return new MockException(400, {
    code: 'Auth:EmailCodeInvalid',
    message: 'The email verification code is incorrect or has expired',
  });
}
//#endif
function getRequestScope(_req: MockRequest): string {
  //#if (IncludeMultiTenancy)
  return _req.headers.get(TENANT_HEADER) ?? 'host';
  //#else
  return 'host';
  //#endif
}

function normalizeEmail(email: string): string {
  return email.trim().toLowerCase();
}
//#if (ExternalLogin)

/** mock 下的外部账号绑定：只演示列表与解绑；绑定要跳真实的提供商，mock 走不通。 */
let mockExternalLinks: {
  id: string;
  provider: string;
  providerAccountLabel: string;
  creationTime: string;
}[] = [
  {
    id: 'mock-link-github',
    provider: 'github',
    providerAccountLabel: 'octocat',
    creationTime: '2026-06-01T00:00:00Z',
  },
];

function externalLoginCallback(req: MockRequest): SessionLoginOutputDto {
  setMockSessionUserId(USERS[0].id);
  setMockSessionTenantKey(getRequestScope(req));
  return {};
}

function getExternalLinks() {
  const user = requireCurrentMockUser();
  return {
    hasPassword: !!user.password,
    providers: availableExternalProviders().map((provider) => ({
      provider,
      link: mockExternalLinks.find((l) => l.provider === provider) ?? null,
    })),
  };
}

/**
 * 外部授权回来后完成绑定。真实后端还要核对一次性票据，Mock 没有提供商往返，视票据有效；
 * 提供商未登记时按票据无效处理（400 `ExternalAuth:InvalidState`），已绑过同一提供商时 409。
 */
function completeExternalLink(req: MockRequest): 'ok' {
  const user = requireCurrentMockUser();
  const provider = String(req.params.provider);
  if (!availableExternalProviders().includes(provider)) {
    throw new MockException(400, {
      code: 'ExternalAuth:InvalidState',
      message: 'Invalid or expired external authentication intent.',
    });
  }
  if (mockExternalLinks.some((l) => l.provider === provider)) {
    throw new MockException(409, {
      code: 'ExternalAuth:ProviderAlreadyLinked',
      message: `A ${provider} account is already linked. Unlink it first.`,
    });
  }
  mockExternalLinks = [
    ...mockExternalLinks,
    {
      id: `mock-link-${provider}`,
      provider,
      providerAccountLabel: user.username,
      creationTime: new Date().toISOString(),
    },
  ];
  return 'ok';
}

function unlinkExternalLogin(req: MockRequest): 'ok' {
  requireCurrentMockUser();
  mockExternalLinks = mockExternalLinks.filter((l) => l.id !== String(req.params.id));
  return 'ok';
}
//#endif

export const AUTH_API = {
  'POST /api/v1/auth/register': (req: MockRequest) => register(req),
  'GET /api/v1/auth/security-config': (req: MockRequest) => getSecurityConfig(req),
  //#if (OpenIddictServer)
  'GET /api/v1/auth/logout-confirmation': (req: MockRequest) => getLogoutConfirmation(req),
  //#endif
  'GET /api/v1/auth/captcha': () => getCaptcha(),
  //#if (Email)
  'POST /api/v1/auth/send-email-code': (req: MockRequest) => sendEmailCode(req),
  //#endif
  'POST /api/v1/auth/logout': () => logout(),
  'POST /api/v1/auth/session-login': (req: MockRequest) => {
    //#if (IncludeMultiTenancy)
    ensureTenantActive(req);
    //#endif
    // 登录是匿名阶段，此时租户提示头决定「凭据在哪个租户内校验」——这是它唯一起作用的地方。
    return sessionLogin(req.body.usernameOrEmail, req.body.password, getRequestScope(req));
  },
  'POST /api/v1/auth/two-factor': (req: MockRequest) => completeTwoFactorLogin(req),
  //#if (Impersonation)
  'GET /api/v1/auth/impersonation': () => getImpersonationStatus(),
  'POST /api/v1/auth/end-impersonation': () => endImpersonation(),
  //#endif
  'GET /api/v1/auth/me': (req: MockRequest) => getCurrentUser(req),
  'PUT /api/v1/auth/me': (req: MockRequest) => updateCurrentUser(req),
  'PUT /api/v1/auth/me/avatar': (req: MockRequest) => setCurrentUserAvatar(req),
  //#if (Email)
  'POST /api/v1/auth/me/email-verification': (req: MockRequest) => sendCurrentUserEmailCode(req),
  //#endif
  //#if (Email)
  'POST /api/v1/auth/me/email-verification/confirm': (req: MockRequest) =>
    confirmCurrentUserEmail(req),
  //#endif
  'GET /api/v1/auth/me/two-factor': () => getTwoFactorStatus(),
  'POST /api/v1/auth/me/two-factor/setup': (req: MockRequest) => beginTwoFactorSetup(req),
  'POST /api/v1/auth/me/two-factor/enable': (req: MockRequest) => enableTwoFactor(req),
  'POST /api/v1/auth/me/two-factor/disable': (req: MockRequest) => disableTwoFactor(req),
  'POST /api/v1/auth/me/two-factor/recovery-codes': (req: MockRequest) =>
    regenerateRecoveryCodes(req),
  'GET /api/v1/auth/me/sessions': () => getSessions(),
  'DELETE /api/v1/auth/me/sessions/:id': (req: MockRequest) => revokeSession(req),
  'POST /api/v1/auth/me/sessions/revoke-others': () => revokeOtherSessions(),
  'POST /api/v1/auth/change-password': (req: MockRequest) => changePassword(req),
  //#if (ExternalLogin)
  'POST /api/v1/external-auth/:provider/complete': (req: MockRequest) => externalLoginCallback(req),
  'POST /api/v1/external-auth/:provider/link/complete': (req: MockRequest) =>
    completeExternalLink(req),
  // 可用的提供方由部署基线叠加宿主设置决定（见 data/settings.ts），在设置页关掉即从这里消失
  'GET /api/v1/external-auth/providers': () => ({ providers: availableExternalProviders() }),
  'GET /api/v1/external-auth/links': () => getExternalLinks(),
  'DELETE /api/v1/external-auth/links/:id': (req: MockRequest) => unlinkExternalLogin(req),
  //#endif
};
