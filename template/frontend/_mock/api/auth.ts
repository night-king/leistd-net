//#if (Impersonation)
import { ImpersonationStatus } from '../../src/app/core/services/impersonation-service';
//#endif
//#if (IncludeMultiTenancy)
import { TENANT_HEADER } from '../../src/app/core/tenancy/tenant-protocol';
//#endif
import { fromBase64Url, toBase64Url } from '../../src/app/core/webauthn/webauthn';
import {
  BeginPasskeyRegistrationInputDto,
  RemovePasskeyInputDto,
  ChangePasswordInputDto,
  CompletePasskeyRegistrationInputDto,
  DisableTwoFactorInputDto,
  BeginTwoFactorSetupInputDto,
  //#if (ExternalLogin)
  CompleteExternalLinkInputDto,
  SetPasswordInputDto,
  UnlinkExternalLoginInputDto,
  //#endif
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
  //#if (Email)
  ResetPasswordInputDto,
  //#endif
  SetAvatarInputDto,
  //#if (Email)
  EmailTwoFactorChangeInputDto,
  TwoFactorEmailCodeOutputDto,
  //#endif
  PasskeyRegistrationOutputDto,
  PasskeySignInInputDto,
  RenamePasskeyInputDto,
  TwoFactorLoginInputDto,
  TwoFactorRecoveryCodesOutputDto,
  TwoFactorSetupOutputDto,
  //#if (OpenIddictServer)
  LogoutConfirmationOutputDto,
  //#endif
} from '../../src/app/features/account/dtos/account.dto';
// prettier-ignore
import {
  PasskeyOptionsOutputDto,
  PasskeyOutputDto,
  SecurityConfigOutputDto,
  //#if (Email)
  TwoFactorEmailUnavailableReason,
  //#endif
  TwoFactorStatusOutputDto,
  UserSessionOutputDto,
} from '../../src/app/shared/dtos/account-security.dto';
import {
  SessionLoginOutputDto,
  TwoFactorMethod,
  UserOutputDto,
} from '../../src/app/shared/dtos/auth.dto';
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
// prettier-ignore
import {
  MOCK_PASSKEYS_AVAILABLE,
  MOCK_TWO_FACTOR_CODE,
  MockPasskey,
  MockUser,
  USERS,
  hasIndependentFactor,
  isEmailTaken,
  isUsernameTaken,
  rotateSecurityVersion,
  toUserOutput,
} from '../data/user';
import {
  beginMockPasskeyCeremony,
  ensureMockReauthentication,
  getCurrentUser as getSessionMockUser,
  //#if (Impersonation)
  getMockImpersonator,
  //#endif
  getMockSessionTenantKey,
  //#if (Impersonation)
  setMockImpersonator,
  //#endif
  mockPasskeyRequestOptions,
  passkeyCeremonyExpired,
  passkeyVerificationFailed,
  readMockWebAuthnCredential,
  setMockSessionTenantKey,
  setMockSessionUserId,
  takeMockPasskeyCeremony,
  verifyMockPasskeyAssertion,
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
  purpose:
    | 'registration-email'
    | 'account-email'
    | 'password-reset'
    | 'two-factor-sign-in'
    | 'two-factor-email-setup';
  email: string;
  code: string;
  expiresAt: number;
  remainingAttempts: number;
  /** 找回挑战绑定的用户与签发时的安全版本；收不到码的挑战两者都没有。 */
  userId?: string;
  securityVersion?: number;
}

/** 找回发码的 24 小时固定窗口上限，与后端一致。 */
const PASSWORD_RESET_DAILY_LIMIT = 10;
const passwordResetWindows = new Map<string, { startedAt: number; count: number }>();

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
    // 与后端一致：有能用的第二步方式时密码对了也不下发会话，只给第二步凭据与可用方式；
    // 口令经邮件找回时第二步只认独立因子
    const firstFactorViaEmail = user.passwordSetByEmailRecovery === true;
    const options = twoFactorOptions(user, tenantKey, firstFactorViaEmail);
    if (options.refused) {
      // 与后端一致：开着第二步、按这次的来路一种都用不了时拒绝，不退回单步
      throw new MockException(400, {
        code: 'Auth:TwoFactorIndependentFactorRequired',
        message:
          'This way of signing in needs your authenticator app or a recovery code. Sign in with your password instead.',
      });
    }
    if (options.defaultMethod) {
      return beginTwoFactorChallenge(user, tenantKey, firstFactorViaEmail);
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
  const user = requireCurrentMockUser();
  // 与后端同一判定：每次按会话所属租户的"要求两步验证"与本人状态得出，不记在会话里；模拟登录的会话不受限
  const requiredByPolicy =
    TENANT_SETTING_VALUES.get(`${getMockSessionTenantKey()}:Security.RequireTwoFactor`) === 'true';
  //#if (Impersonation)
  const restrictable = getMockImpersonator() === null;
  //#else
  const restrictable = true;
  //#endif
  return {
    ...toUserOutput(user),
    twoFactorSetupRequired: requiredByPolicy && restrictable && !hasIndependentFactor(user),
  };
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

  // 换了邮箱即回到未验证，与后端 User.UpdateProfile 同一规则；换登录邮箱要先确认是本人
  if (normalizeEmail(user.email) !== normalizeEmail(email)) {
    ensureMockReauthentication(user, {
      password: body.currentPassword,
      twoFactorCode: body.twoFactorCode,
      passkey: body.passkey,
    });
    user.isEmailVerified = false;
    //#if (Email)
    // 与后端 User.UpdateProfile 一致：邮箱验证码证明的是旧地址，关掉它；其他设备随之退出
    if (user.emailTwoFactorEnabled) {
      user.emailTwoFactorEnabled = false;
      if (user.preferredTwoFactorMethod === 'email') {
        user.preferredTwoFactorMethod = undefined;
      }
      mockSessions = mockSessions.filter((s) => s.isCurrent);
    }
    //#endif
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
  user.passwordSetByEmailRecovery = false;
  rotateSecurityVersion(user);
  // 与服务端一致：改密码后其他设备退出登录
  mockSessions = mockSessions.filter((s) => s.isCurrent);
  return 'ok';
}
//#if (ExternalLogin)

/**
 * 没有本地口令的账号设置第一个口令。已有口令时 409；会话不够新时要出示验证码（账号没有口令可出示），
 * 两者都没有时只能重新登录。与改口令一致，成功后其他设备退出登录。
 */
function setPassword(req: MockRequest): 'ok' {
  const user = requireCurrentMockUser();
  const body = (req.body ?? {}) as SetPasswordInputDto;
  if (user.password) {
    throw new MockException(409, {
      code: 'Security:PasswordAlreadySet',
      message: 'A password is already set. Use change password instead.',
    });
  }
  if (!body.newPassword || body.newPassword !== body.confirmPassword) {
    throw new MockException(400, {
      errors: [{ field: 'confirmPassword', detail: 'The new passwords do not match.' }],
    });
  }
  ensureAcceptablePassword(body.newPassword, 'New password');
  ensureMockReauthentication(user, { twoFactorCode: body.twoFactorCode, passkey: body.passkey });

  user.password = body.newPassword;
  rotateSecurityVersion(user);
  mockSessions = mockSessions.filter((s) => s.isCurrent);
  return 'ok';
}
//#endif

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
 * 两步验证：mock 下验证码固定为 `MOCK_TWO_FACTOR_CODE`，启用状态与恢复码记在各 Mock 用户上，
 * 因此登录第二步与管理员重置都能演示。
 */
/** 与后端单个挑战的尝试上限同一量级：输错到上限即作废，只能回到第一步。 */
const TWO_FACTOR_CHALLENGE_MAX_ATTEMPTS = 5;

interface TwoFactorChallenge {
  userId: string;
  tenantKey: string;
  remainingAttempts: number;
  /** 第一步经邮箱通道得来（口令经邮件找回）：第二步只认独立因子。 */
  firstFactorViaEmail: boolean;
  /** 最近一次取通行密钥断言选项时的挑战；只认它，再取一次即替换。 */
  passkeyChallenge?: string;
  //#if (Email)
  /** 最近一次发出的邮箱验证码挑战；只认它。 */
  emailChallengeId?: string;
  //#endif
}

const twoFactorChallengeStore = new Map<string, TwoFactorChallenge>();
//#if (Email)
/** 登录第二步限频键 → 占着发送间隔的那封邮件挑战：间隔内再发时沿用它。 */
const signInEmailChallenges = new Map<string, string>();
//#endif

/**
 * 本人此刻能用的第二步方式与默认方式，与后端 `TwoFactorMethodResolver.ResolveForSignInAsync` 同一规则：
 * 系统顺序 通行密钥 → 身份验证器 → 邮箱验证码；第一步经邮箱通道得来，或组织要求两步验证而本人有独立因子时，
 * 不给邮箱验证码（它不是独立因子）。恢复码只作备选，其余方式都用不了时才作默认。
 */
function twoFactorOptions(
  user: MockUser,
  tenantKey: string,
  firstFactorViaEmail = false,
): {
  methods: TwoFactorMethod[];
  defaultMethod: TwoFactorMethod | null;
  maskedEmail: string | null;
  /** 开着邮箱验证码、组织也允许，但这次只认独立因子而本人没有：拒绝登录。 */
  refused: boolean;
} {
  const methods: TwoFactorMethod[] = [];
  if (MOCK_PASSKEYS_AVAILABLE && (user.passkeys?.length ?? 0) > 0) {
    methods.push('passkey');
  }
  if (user.twoFactorEnabled) {
    methods.push('authenticator');
  }
  //#if (Email)
  const independentOnly =
    firstFactorViaEmail ||
    (hasIndependentFactor(user) &&
      TENANT_SETTING_VALUES.get(`${tenantKey}:Security.RequireTwoFactor`) === 'true');
  const emailUsable =
    user.emailTwoFactorEnabled === true &&
    !emailTwoFactorUnavailableReason(user, tenantKey) &&
    !independentOnly;
  if (emailUsable) {
    methods.push('email');
  }
  const maskedEmail = emailUsable ? maskEmail(user.email) : null;
  const refused =
    independentOnly &&
    !hasIndependentFactor(user) &&
    user.emailTwoFactorEnabled === true &&
    !emailTwoFactorUnavailableReason(user, tenantKey);
  //#else
  // 不含邮件能力时只有独立因子，与租户的设置无关
  void tenantKey;
  void firstFactorViaEmail;
  const maskedEmail = null;
  const refused = false;
  //#endif
  const preferred = user.preferredTwoFactorMethod;
  let defaultMethod: TwoFactorMethod | null =
    preferred && methods.includes(preferred) ? preferred : (methods[0] ?? null);
  if (hasIndependentFactor(user) && (user.recoveryCodes?.length ?? 0) > 0) {
    methods.push('recoveryCode');
    defaultMethod ??= 'recoveryCode';
  }
  return { methods, defaultMethod, maskedEmail, refused };
}
//#if (Email)

/** 邮箱验证码为什么用不了：组织未允许、邮箱未验证；能用时为 null。 */
function emailTwoFactorUnavailableReason(
  user: MockUser,
  tenantKey: string,
): TwoFactorEmailUnavailableReason | null {
  if (TENANT_SETTING_VALUES.get(`${tenantKey}:Security.AllowEmailTwoFactor`) !== 'true') {
    return 'notAllowed';
  }
  return user.isEmailVerified ? null : 'emailNotVerified';
}

/** 与后端 `TextRedactor.RedactEmail` 同一形态：本地部最多留前 3 位且不超过一半，域名完整保留。 */
function maskEmail(email: string): string {
  const at = email.lastIndexOf('@');
  if (at <= 0) {
    return '***';
  }
  const local = email.slice(0, at);
  const keep = Math.min(3, Math.floor(local.length / 2));
  return `${local.slice(0, keep)}***${email.slice(at)}`;
}
//#endif

function twoFactorMethodUnavailable(): MockException {
  return new MockException(400, {
    code: 'Auth:TwoFactorMethodUnavailable',
    message: "This verification method isn't available for your account.",
  });
}

function beginTwoFactorChallenge(
  user: MockUser,
  tenantKey: string,
  firstFactorViaEmail = false,
): SessionLoginOutputDto {
  const twoFactorToken = crypto.randomUUID();
  twoFactorChallengeStore.set(twoFactorToken, {
    userId: user.id,
    tenantKey,
    remainingAttempts: TWO_FACTOR_CHALLENGE_MAX_ATTEMPTS,
    firstFactorViaEmail,
  });
  const options = twoFactorOptions(user, tenantKey, firstFactorViaEmail);
  return {
    requiresTwoFactor: true,
    twoFactorToken,
    twoFactorMethods: options.methods,
    defaultTwoFactorMethod: options.defaultMethod,
    maskedEmail: options.maskedEmail,
  };
}
//#if (Email)

/** 登录第二步发邮箱验证码：Mock 下码固定为 EMAIL_CODE，按收件地址限制发送间隔。 */
function sendTwoFactorEmailCode(req: MockRequest): TwoFactorEmailCodeOutputDto {
  const token = (req.body as { token?: string } | undefined)?.token ?? '';
  const challenge = twoFactorChallengeStore.get(token);
  const user = challenge ? USERS.find((u) => u.id === challenge.userId) : undefined;
  if (!challenge || !user || challenge.tenantKey !== getRequestScope(req)) {
    throw twoFactorChallengeExpired();
  }
  if (
    !twoFactorOptions(user, challenge.tenantKey, challenge.firstFactorViaEmail).methods.includes(
      'email',
    )
  ) {
    throw twoFactorMethodUnavailable();
  }

  // 与后端一致：登录第二步单独一把限频键（找回口令不挡它）；间隔内再发沿用上一封，回报剩余间隔
  const email = normalizeEmail(user.email);
  const rateKey = `${challenge.tenantKey}:sign-in:${email}`;
  const retryUntil = emailRateLimitStore.get(rateKey) ?? 0;
  const previous = signInEmailChallenges.get(rateKey);
  const previousChallenge = previous ? emailChallengeStore.get(previous) : undefined;
  if (retryUntil > Date.now()) {
    if (previousChallenge?.userId === user.id && previousChallenge.expiresAt > Date.now()) {
      challenge.emailChallengeId = previous;
      return {
        expiresInSeconds: Math.ceil((previousChallenge.expiresAt - Date.now()) / 1000),
        retryAfterSeconds: Math.max(1, Math.ceil((retryUntil - Date.now()) / 1000)),
      };
    }
    throw new MockException(429, {
      code: 'Auth:EmailCodeSendTooFrequent',
      message: 'Verification codes are being sent too frequently. Please try again later.',
    });
  }
  emailRateLimitStore.set(rateKey, Date.now() + EMAIL_CODE_RETRY_SECONDS * 1000);

  const emailChallengeId = crypto.randomUUID();
  emailChallengeStore.set(emailChallengeId, {
    scope: challenge.tenantKey,
    purpose: 'two-factor-sign-in',
    email,
    code: EMAIL_CODE,
    expiresAt: Date.now() + EMAIL_CODE_EXPIRY_SECONDS * 1000,
    remainingAttempts: EMAIL_CODE_MAX_ATTEMPTS,
    userId: user.id,
  });
  signInEmailChallenges.set(rateKey, emailChallengeId);
  challenge.emailChallengeId = emailChallengeId;
  return {
    expiresInSeconds: EMAIL_CODE_EXPIRY_SECONDS,
    retryAfterSeconds: EMAIL_CODE_RETRY_SECONDS,
  };
}
//#endif

/** 第二步：取用通行密钥验证的断言选项；每取一次换一个挑战，之前的随之不再被认。 */
function beginTwoFactorPasskey(req: MockRequest): PasskeyOptionsOutputDto {
  const token = (req.body as { token?: string } | undefined)?.token ?? '';
  const challenge = twoFactorChallengeStore.get(token);
  const user = challenge ? USERS.find((u) => u.id === challenge.userId) : undefined;
  if (!challenge || !user || challenge.tenantKey !== getRequestScope(req)) {
    throw twoFactorChallengeExpired();
  }
  if (
    !twoFactorOptions(user, challenge.tenantKey, challenge.firstFactorViaEmail).methods.includes(
      'passkey',
    )
  ) {
    throw twoFactorMethodUnavailable();
  }
  challenge.passkeyChallenge = beginMockPasskeyCeremony(`two-factor:${token}`);
  return { options: mockPasskeyRequestOptions(challenge.passkeyChallenge, user.passkeys ?? []) };
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
  const passkey = typeof body.passkey === 'object' && body.passkey !== null ? body.passkey : null;
  //#if (Email)
  const presented =
    [body.code, body.emailCode, body.recoveryCode].filter((v) => v?.trim()).length +
    (passkey ? 1 : 0);
  //#else
  const presented =
    [body.code, body.recoveryCode].filter((v) => v?.trim()).length + (passkey ? 1 : 0);
  //#endif
  if (presented === 0) {
    errors.push({ field: 'code', detail: 'Enter the verification code or a recovery code.' });
  } else if (presented > 1) {
    // 与后端一致：恰好一种，多给不按优先级挑
    errors.push({ field: 'code', detail: 'Enter only one kind of code.' });
  }
  if (errors.length > 0) {
    throw new MockException(400, { errors });
  }

  const challenge = twoFactorChallengeStore.get(body.token);
  const user = challenge ? USERS.find((u) => u.id === challenge.userId) : undefined;
  if (!challenge || !user || challenge.tenantKey !== getRequestScope(req)) {
    throw twoFactorChallengeExpired();
  }

  // 入参已保证恰好一种；恢复码用一次即作废
  const recoveryCode = body.recoveryCode?.trim();
  let verified: boolean;
  if (passkey) {
    verified = verifyTwoFactorPasskey(user, challenge, passkey);
  } else if (recoveryCode) {
    verified = (user.recoveryCodes ?? []).includes(recoveryCode);
  } else {
    //#if (Email)
    const emailCode = body.emailCode?.trim();
    verified = emailCode
      ? verifyTwoFactorEmailCode(user, challenge, emailCode)
      : verifyAuthenticatorCode(user, body.code);
    //#else
    verified = verifyAuthenticatorCode(user, body.code);
    //#endif
  }
  if (!verified) {
    challenge.remainingAttempts--;
    if (challenge.remainingAttempts <= 0) {
      twoFactorChallengeStore.delete(body.token);
      throw twoFactorChallengeExpired();
    }
    if (passkey) {
      throw passkeyVerificationFailed();
    }
    throw new MockException(400, {
      code: 'Auth:TwoFactorCodeInvalid',
      message: `The verification code is incorrect (mock code: ${MOCK_TWO_FACTOR_CODE}).`,
    });
  }

  if (recoveryCode) {
    user.recoveryCodes = (user.recoveryCodes ?? []).filter((code) => code !== recoveryCode);
  }
  //#if (Email)
  // 用独立因子（身份验证器、恢复码）完成了第二步：找回得来的口令不再只凭邮箱
  if (!body.emailCode?.trim()) {
    user.passwordSetByEmailRecovery = false;
  }
  //#endif
  twoFactorChallengeStore.delete(body.token);
  setMockSessionUserId(user.id);
  setMockSessionTenantKey(challenge.tenantKey);
  return 'ok';
}

//#if (Email)
// 方式此刻不能用不算猜错；码只认这次第二步最近发出的那一个
function verifyTwoFactorEmailCode(
  user: MockUser,
  challenge: TwoFactorChallenge,
  code: string,
): boolean {
  if (
    !twoFactorOptions(user, challenge.tenantKey, challenge.firstFactorViaEmail).methods.includes(
      'email',
    )
  ) {
    throw twoFactorMethodUnavailable();
  }
  const emailChallenge = challenge.emailChallengeId
    ? emailChallengeStore.get(challenge.emailChallengeId)
    : undefined;
  // 没有仍有效的邮件挑战（没发过、已过期、次数用完）不算猜错，与后端一样提示重新获取
  if (
    !emailChallenge ||
    emailChallenge.purpose !== 'two-factor-sign-in' ||
    emailChallenge.userId !== user.id ||
    emailChallenge.expiresAt <= Date.now() ||
    emailChallenge.remainingAttempts <= 0
  ) {
    throw new MockException(400, {
      code: 'Auth:EmailCodeExpired',
      message: 'The email verification code has expired. Send a new one.',
    });
  }
  if (emailChallenge.code !== code) {
    emailChallenge.remainingAttempts--;
    return false;
  }
  emailChallengeStore.delete(challenge.emailChallengeId!);
  return true;
}

//#endif
// 方式此刻不能用、还没取过选项不算猜错；凭据不是这个人登记的（或挑战对不上）按验证失败计数
function verifyTwoFactorPasskey(
  user: MockUser,
  challenge: TwoFactorChallenge,
  passkey: unknown,
): boolean {
  if (
    !twoFactorOptions(user, challenge.tenantKey, challenge.firstFactorViaEmail).methods.includes(
      'passkey',
    )
  ) {
    throw twoFactorMethodUnavailable();
  }
  if (!challenge.passkeyChallenge) {
    throw passkeyCeremonyExpired();
  }
  return verifyMockPasskeyAssertion(passkey, challenge.passkeyChallenge, user) !== null;
}

// 与后端一致：没启用身份验证器的人（只开了邮箱验证码）交来的验证码一律不认
function verifyAuthenticatorCode(user: MockUser, code: string | undefined): boolean {
  return user.twoFactorEnabled === true && code?.replace(/\s/g, '') === MOCK_TWO_FACTOR_CODE;
}

/** 发一组新的恢复码（账号级），旧的随即作废。 */
function issueRecoveryCodes(user: MockUser): string[] {
  const codes = Array.from(
    { length: 10 },
    (_, i) => `mock-${String(i).padStart(4, '0')}-code-demo`,
  );
  user.recoveryCodes = [...codes];
  return codes;
}

/** 最后一个独立因子去掉了：恢复码随之作废（与后端 `User.ReleaseRecoveryCodesWithoutIndependentFactor` 一致）。 */
function releaseRecoveryCodesWithoutIndependentFactor(user: MockUser): void {
  if (!hasIndependentFactor(user)) {
    user.recoveryCodes = [];
  }
}

function isTwoFactorRequiredByPolicy(tenantKey: string): boolean {
  return TENANT_SETTING_VALUES.get(`${tenantKey}:Security.RequireTwoFactor`) === 'true';
}

function twoFactorRequiredByPolicy(): MockException {
  return new MockException(403, {
    code: 'Auth:TwoFactorRequiredByPolicy',
    message: 'Two-factor authentication is required here and cannot be turned off.',
  });
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
  const tenantKey = getMockSessionTenantKey();
  return {
    enabled: user.twoFactorEnabled === true,
    passkeys: (user.passkeys ?? []).map(toPasskeyOutput),
    passkeysAvailable: MOCK_PASSKEYS_AVAILABLE,
    recoveryCodesLeft: hasIndependentFactor(user) ? (user.recoveryCodes?.length ?? 0) : 0,
    requiredByPolicy: isTwoFactorRequiredByPolicy(tenantKey),
    preferredMethod: user.preferredTwoFactorMethod ?? null,
    defaultMethod: twoFactorOptions(user, tenantKey).defaultMethod,
    //#if (Email)
    emailEnabled: user.emailTwoFactorEnabled === true,
    emailUnavailableReason: emailTwoFactorUnavailableReason(user, tenantKey),
    maskedEmail: maskEmail(user.email),
    //#endif
  };
}
//#if (Email)

/** 开始开启邮箱验证码：确认是本人后发确认码（Mock 下固定为 EMAIL_CODE）。 */
function beginEmailTwoFactorSetup(req: MockRequest): EmailVerificationChallengeOutputDto {
  const user = requireCurrentMockUser();
  const body = (req.body ?? {}) as EmailTwoFactorChangeInputDto;
  ensureEmailTwoFactorCanBeEnabled(user);
  ensureMockReauthentication(user, {
    password: body.password,
    twoFactorCode: body.twoFactorCode,
    passkey: body.passkey,
  });

  const challengeId = crypto.randomUUID();
  emailChallengeStore.set(challengeId, {
    scope: getMockSessionTenantKey(),
    purpose: 'two-factor-email-setup',
    email: normalizeEmail(user.email),
    code: EMAIL_CODE,
    expiresAt: Date.now() + EMAIL_CODE_EXPIRY_SECONDS * 1000,
    remainingAttempts: EMAIL_CODE_MAX_ATTEMPTS,
    userId: user.id,
  });
  return {
    challengeId,
    expiresInSeconds: EMAIL_CODE_EXPIRY_SECONDS,
    retryAfterSeconds: EMAIL_CODE_RETRY_SECONDS,
  };
}

function enableEmailTwoFactor(req: MockRequest): 'ok' {
  const user = requireCurrentMockUser();
  const body = req.body as EmailVerificationInputDto;
  ensureEmailTwoFactorCanBeEnabled(user);
  const challenge = emailChallengeStore.get(body.challengeId);
  if (challenge && challenge.expiresAt <= Date.now()) {
    emailChallengeStore.delete(body.challengeId);
  }
  if (
    !challenge ||
    challenge.expiresAt <= Date.now() ||
    challenge.purpose !== 'two-factor-email-setup' ||
    challenge.userId !== user.id ||
    challenge.email !== normalizeEmail(user.email) ||
    challenge.code !== body.code.trim()
  ) {
    throw new MockException(400, {
      code: 'Auth:EmailCodeInvalid',
      message: 'The email verification code is incorrect or has expired.',
    });
  }
  emailChallengeStore.delete(body.challengeId);
  user.emailTwoFactorEnabled = true;
  // 与后端一致：本人在会话里开启了第二步，口令不再视为只凭邮箱得来
  user.passwordSetByEmailRecovery = false;
  return 'ok';
}

function disableEmailTwoFactor(req: MockRequest): 'ok' {
  const user = requireCurrentMockUser();
  if (!user.emailTwoFactorEnabled) {
    return 'ok';
  }
  const body = (req.body ?? {}) as EmailTwoFactorChangeInputDto;
  ensureMockReauthentication(user, {
    password: body.password,
    twoFactorCode: body.twoFactorCode,
    passkey: body.passkey,
  });
  user.emailTwoFactorEnabled = false;
  if (user.preferredTwoFactorMethod === 'email') {
    user.preferredTwoFactorMethod = undefined;
  }
  return 'ok';
}

function ensureEmailTwoFactorCanBeEnabled(user: MockUser): void {
  if (user.emailTwoFactorEnabled) {
    throw new MockException(409, {
      code: 'Auth:TwoFactorAlreadyEnabled',
      message: 'Email verification codes are already turned on.',
    });
  }
  if (emailTwoFactorUnavailableReason(user, getMockSessionTenantKey())) {
    throw twoFactorMethodUnavailable();
  }
}
//#endif

/** 指定首选方式：只能是本人已设置的通行密钥、身份验证器或邮箱验证码；恢复码只作备选。 */
function setPreferredTwoFactorMethod(req: MockRequest): 'ok' {
  const user = requireCurrentMockUser();
  const method = (req.body as { method?: TwoFactorMethod } | undefined)?.method;
  const configured =
    (method === 'passkey' && (user.passkeys?.length ?? 0) > 0) ||
    (method === 'authenticator' && user.twoFactorEnabled === true) ||
    (method === 'email' && user.emailTwoFactorEnabled === true);
  if (!configured) {
    throw twoFactorMethodUnavailable();
  }
  user.preferredTwoFactorMethod = method as 'passkey' | 'authenticator' | 'email';
  return 'ok';
}

/** 最近一次开始的设置：只认它，别处重新开始过就作废（同后端）。 */
let mockPendingSetupId: string | null = null;

/**
 * 开始设置或换绑。首次启用时会话不够新要出示口令；换绑时现有验证器上的码同时算确认身份，只校验一次，
 * 不能再同时带口令；验证器丢了用恢复码（它不算确认身份，会话不够新时仍要口令），确认身份通过后才用掉。
 */
function beginTwoFactorSetup(req: MockRequest): TwoFactorSetupOutputDto {
  const user = requireCurrentMockUser();
  const body = (req.body ?? {}) as BeginTwoFactorSetupInputDto;
  if (user.twoFactorEnabled) {
    const recoveryCode = body.recoveryCode?.trim();
    if (body.code) {
      ensureMockReauthentication(user, { password: body.password, twoFactorCode: body.code });
    } else if (recoveryCode) {
      ensureMockReauthentication(user, { password: body.password, passkey: body.passkey });
      const index = (user.recoveryCodes ?? []).indexOf(recoveryCode);
      if (index < 0) {
        ensureMockTwoFactorCode(undefined);
      }
      user.recoveryCodes = (user.recoveryCodes ?? []).filter((_, i) => i !== index);
    } else {
      throw new MockException(409, {
        code: 'Auth:TwoFactorAlreadyEnabled',
        message: 'Two-factor authentication is already turned on.',
      });
    }
  } else {
    ensureMockReauthentication(user, { password: body.password, passkey: body.passkey });
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
  user.passwordSetByEmailRecovery = false;
  // 换绑一并换掉恢复码；首次启用时它是第一个独立因子才发，已有通行密钥时恢复码不变
  const replacing = user.twoFactorEnabled === true;
  const firstFactor = !hasIndependentFactor(user);
  user.twoFactorEnabled = true;
  return { recoveryCodes: replacing || firstFactor ? issueRecoveryCodes(user) : [] };
}

/** 停用两步验证：有口令的账号口令与验证码都要；没有口令的账号只要验证码，带了口令按方式不具备拒绝。 */
function disableTwoFactor(req: MockRequest): 'ok' {
  const user = requireCurrentMockUser();
  const body = (req.body ?? {}) as Partial<DisableTwoFactorInputDto>;
  // 与后端一致：组织要求两步验证时不能拆掉最后一个独立因子；还有通行密钥就照常停用
  if (
    user.twoFactorEnabled === true &&
    (user.passkeys?.length ?? 0) === 0 &&
    isTwoFactorRequiredByPolicy(getMockSessionTenantKey())
  ) {
    throw twoFactorRequiredByPolicy();
  }
  if (user.password) {
    if (!body.password) {
      throw new MockException(400, {
        code: 'Security:PasswordRequired',
        message: 'Enter your current password.',
      });
    }
    if (body.password !== user.password) {
      throw new MockException(400, {
        code: 'Security:CurrentPasswordIncorrect',
        message: 'The current password is incorrect.',
      });
    }
  } else if (body.password) {
    throw new MockException(400, {
      code: 'Auth:ReauthenticationMethodUnavailable',
      message: 'This way of confirming your identity is not available for your account.',
    });
  }
  ensureMockTwoFactorCode(body.code);
  user.twoFactorEnabled = false;
  if (user.preferredTwoFactorMethod === 'authenticator') {
    user.preferredTwoFactorMethod = undefined;
  }
  releaseRecoveryCodesWithoutIndependentFactor(user);
  mockSessions = mockSessions.filter((s) => s.isCurrent);
  return 'ok';
}

/** 重新生成恢复码：出示一个独立因子（身份验证器上的验证码或通行密钥断言），恰好一种；不认口令与恢复码。 */
function regenerateRecoveryCodes(req: MockRequest): TwoFactorRecoveryCodesOutputDto {
  const user = requireCurrentMockUser();
  const body = (req.body ?? {}) as { code?: string; passkey?: unknown };
  const passkey = typeof body.passkey === 'object' && body.passkey !== null ? body.passkey : null;
  const presented = (body.code?.trim() ? 1 : 0) + (passkey ? 1 : 0);
  if (presented !== 1) {
    throw new MockException(400, {
      errors: [
        {
          field: 'code',
          detail:
            presented === 0
              ? 'Enter the verification code or a recovery code.'
              : 'Enter only one kind of code.',
        },
      ],
    });
  }
  if (!hasIndependentFactor(user)) {
    throw new MockException(409, {
      code: 'Auth:TwoFactorNotEnabled',
      message: 'Two-factor authentication is not turned on.',
    });
  }
  if (passkey) {
    ensureMockReauthentication(user, { passkey });
  } else if (user.twoFactorEnabled !== true) {
    ensureMockTwoFactorCode(undefined);
  } else {
    ensureMockTwoFactorCode(body.code?.replace(/\s/g, ''));
  }
  return { recoveryCodes: issueRecoveryCodes(user) };
}

/** 免口令登录：给出不限定凭据的断言选项与仪式凭据；仪式在这一刻绑定请求的租户。 */
function beginPasskeySignIn(req: MockRequest): PasskeyOptionsOutputDto {
  if (!MOCK_PASSKEYS_AVAILABLE) {
    throw new MockException(400, {
      code: 'Auth:PasskeyUnavailable',
      message: "Passkeys aren't available on this site right now. Contact your administrator.",
    });
  }
  const ceremony = crypto.randomUUID();
  const challenge = beginMockPasskeyCeremony(`sign-in:${getRequestScope(req)}:${ceremony}`);
  return { options: mockPasskeyRequestOptions(challenge), ceremony };
}

/**
 * 免口令登录：认出已登记的凭据即签发会话，不再要求第二步，也不看口令是不是经邮件找回的。
 * 仪式只认一次；换了租户、认不出凭据都按失效或验证失败拒绝；停用、锁定的账号不能登录。
 */
function completePasskeySignIn(req: MockRequest): 'ok' {
  const body = (req.body ?? {}) as Partial<PasskeySignInInputDto>;
  const errors = [];
  if (!body.ceremony?.trim()) {
    errors.push({ field: 'ceremony', detail: 'Ceremony is required.' });
  }
  if (typeof body.credential !== 'object' || body.credential === null) {
    errors.push({ field: 'credential', detail: 'Credential is required.' });
  }
  if (errors.length > 0) {
    throw new MockException(400, { errors });
  }
  const tenantKey = getRequestScope(req);
  const challenge = takeMockPasskeyCeremony(`sign-in:${tenantKey}:${body.ceremony}`);
  if (!challenge) {
    throw passkeyCeremonyExpired();
  }
  const match = verifyMockPasskeyAssertion(body.credential, challenge);
  if (!match) {
    throw passkeyVerificationFailed();
  }
  ensureMockUserCanSignIn(match.user);
  match.user.passwordSetByEmailRecovery = false;
  match.user.lastLoginTime = new Date().toISOString();
  setMockSessionUserId(match.user.id);
  setMockSessionTenantKey(tenantKey);
  return 'ok';
}

function ensureMockUserCanSignIn(user: MockUser): void {
  if (!user.isActive) {
    throw new MockException(401, {
      code: 'Auth:UserDisabled',
      message: 'This account is disabled. Contact your administrator.',
    });
  }
  if (
    user.lockoutEnd === null ||
    (user.lockoutEnd !== undefined && new Date(user.lockoutEnd).getTime() > Date.now())
  ) {
    throw new MockException(401, {
      code: 'Auth:UserLockedOut',
      message: 'This account is locked. Contact your administrator.',
    });
  }
}

/** 确认身份：取用通行密钥确认的断言选项；断言随要求确认的请求一起提交。 */
function beginPasskeyReauthentication(): PasskeyOptionsOutputDto {
  const user = requireCurrentMockUser();
  ensurePasskeysAvailable();
  if ((user.passkeys?.length ?? 0) === 0) {
    throw new MockException(400, {
      code: 'Auth:ReauthenticationMethodUnavailable',
      message: "This verification method isn't available for your account.",
    });
  }
  const challenge = beginMockPasskeyCeremony(`reauthentication:${user.id}`);
  return { options: mockPasskeyRequestOptions(challenge, user.passkeys ?? []) };
}

/** 开始添加通行密钥：先确认是本人，再给出创建选项（排除本人已有的凭据）。 */
function beginPasskeyRegistration(req: MockRequest): PasskeyOptionsOutputDto {
  const user = requireCurrentMockUser();
  ensurePasskeysAvailable();
  const body = (req.body ?? {}) as BeginPasskeyRegistrationInputDto;
  ensureMockReauthentication(user, {
    password: body.password,
    twoFactorCode: body.twoFactorCode,
    passkey: body.passkey,
  });
  const challenge = beginMockPasskeyCeremony(`registration:${user.id}`);
  return {
    options: {
      rp: { name: 'CompanyName.ProjectName' },
      user: {
        id: toBase64Url(new TextEncoder().encode(user.id)),
        name: user.username,
        displayName: user.displayName ?? user.username,
      },
      challenge,
      pubKeyCredParams: [
        { type: 'public-key', alg: -7 },
        { type: 'public-key', alg: -257 },
      ],
      timeout: 300_000,
      attestation: 'none',
      authenticatorSelection: {
        residentKey: 'preferred',
        requireResidentKey: false,
        userVerification: 'required',
      },
      excludeCredentials: (user.passkeys ?? []).map((passkey) => ({
        type: 'public-key',
        id: passkey.credentialId,
      })),
    },
  };
}

/**
 * 完成添加：只核对这次挑战与凭据 Id 没被占用，保存浏览器交回的 Id，不校验证明与公钥。
 * 它是第一个独立因子时一并发恢复码；本人的其他设备随之退出。
 */
function completePasskeyRegistration(req: MockRequest): PasskeyRegistrationOutputDto {
  const user = requireCurrentMockUser();
  ensurePasskeysAvailable();
  const body = (req.body ?? {}) as Partial<CompletePasskeyRegistrationInputDto>;
  const name = ensurePasskeyName(body.name);
  if (typeof body.credential !== 'object' || body.credential === null) {
    throw new MockException(400, {
      errors: [{ field: 'credential', detail: 'Credential is required.' }],
    });
  }
  const challenge = takeMockPasskeyCeremony(`registration:${user.id}`);
  if (!challenge) {
    throw passkeyCeremonyExpired();
  }
  const data = readMockWebAuthnCredential(body.credential);
  if (!data || data.type !== 'webauthn.create' || data.challenge !== challenge) {
    throw passkeyVerificationFailed();
  }
  if (USERS.some((u) => u.passkeys?.some((p) => p.credentialId === data.credentialId))) {
    throw new MockException(409, {
      code: 'Auth:PasskeyAlreadyRegistered',
      message: 'This passkey is already registered.',
    });
  }

  const flags = backupFlags(body.credential);
  const passkey: MockPasskey = {
    id: crypto.randomUUID(),
    credentialId: data.credentialId,
    name,
    creationTime: new Date().toISOString(),
    lastUsedTime: null,
    isBackupEligible: flags.eligible,
    isBackedUp: flags.backedUp,
  };
  const firstFactor = !hasIndependentFactor(user);
  user.passkeys = [...(user.passkeys ?? []), passkey];
  user.passwordSetByEmailRecovery = false;
  mockSessions = mockSessions.filter((s) => s.isCurrent);
  return {
    passkey: toPasskeyOutput(passkey),
    recoveryCodes: firstFactor ? issueRecoveryCodes(user) : [],
  };
}

/** 改名：只改显示用的名称，不要求确认身份。 */
function renamePasskey(req: MockRequest): PasskeyOutputDto {
  const user = requireCurrentMockUser();
  const passkey = findOwnPasskey(user, String(req.params.id));
  passkey.name = ensurePasskeyName((req.body as Partial<RenamePasskeyInputDto> | undefined)?.name);
  return toPasskeyOutput(passkey);
}

/**
 * 删除：组织要求两步验证、它又是最后一个独立因子时拒绝（先于确认身份）；确认身份后删除。
 * 最后一个独立因子去掉时恢复码作废，指向它的首选回到系统顺序，其他设备随之退出。
 */
function removePasskey(req: MockRequest): 'ok' {
  const user = requireCurrentMockUser();
  const passkey = findOwnPasskey(user, String(req.params.id));
  if (
    user.twoFactorEnabled !== true &&
    (user.passkeys?.length ?? 0) === 1 &&
    isTwoFactorRequiredByPolicy(getMockSessionTenantKey())
  ) {
    throw twoFactorRequiredByPolicy();
  }
  const body = (req.body ?? {}) as RemovePasskeyInputDto;
  ensureMockReauthentication(user, {
    password: body.password,
    twoFactorCode: body.twoFactorCode,
    passkey: body.passkey,
  });
  user.passkeys = (user.passkeys ?? []).filter((item) => item.id !== passkey.id);
  if (user.preferredTwoFactorMethod === 'passkey' && user.passkeys.length === 0) {
    user.preferredTwoFactorMethod = undefined;
  }
  releaseRecoveryCodesWithoutIndependentFactor(user);
  mockSessions = mockSessions.filter((s) => s.isCurrent);
  return 'ok';
}

function ensurePasskeysAvailable(): void {
  if (!MOCK_PASSKEYS_AVAILABLE) {
    throw new MockException(400, {
      code: 'Auth:PasskeyUnavailable',
      message: "Passkeys aren't available on this site right now. Contact your administrator.",
    });
  }
}

function findOwnPasskey(user: MockUser, id: string): MockPasskey {
  const passkey = user.passkeys?.find((item) => item.id === id);
  if (!passkey) {
    throw new MockException(404, {
      code: 'Auth:PasskeyNotFound',
      message: "This passkey doesn't exist.",
    });
  }
  return passkey;
}

/** 名称必填、最长 128，与后端入参校验一致（400 字段错误，不带业务码）。 */
function ensurePasskeyName(value: string | undefined): string {
  const name = value?.trim() ?? '';
  if (!name || name.length > 128) {
    throw new MockException(400, {
      errors: [
        {
          field: 'name',
          detail: name ? 'Name cannot exceed 128 characters.' : 'Name is required.',
        },
      ],
    });
  }
  return name;
}

function toPasskeyOutput(passkey: MockPasskey): PasskeyOutputDto {
  return {
    id: passkey.id,
    name: passkey.name,
    creationTime: passkey.creationTime,
    lastUsedTime: passkey.lastUsedTime ?? null,
    isBackupEligible: passkey.isBackupEligible,
    isBackedUp: passkey.isBackedUp,
  };
}

/**
 * 从证明对象（CBOR）里取认证器数据的备份标志（BE、BS），让列表的备份状态与真实认证器一致
 * （Chrome 虚拟认证器可以设置）。只找 `authData` 这个键后面的字节串，解析不了时按不可备份处理。
 */
function backupFlags(credential: unknown): { eligible: boolean; backedUp: boolean } {
  const attestation = (credential as { response?: { attestationObject?: unknown } }).response
    ?.attestationObject;
  if (typeof attestation !== 'string') {
    return { eligible: false, backedUp: false };
  }
  try {
    const bytes = fromBase64Url(attestation);
    const key = [0x68, ...new TextEncoder().encode('authData')];
    const at = bytes.findIndex((_, i) => key.every((byte, offset) => bytes[i + offset] === byte));
    if (at < 0) {
      return { eligible: false, backedUp: false };
    }
    const header = bytes[at + key.length];
    // 字节串头：0x58 带 1 字节长度、0x59 带 2 字节长度；认证器数据的第 33 字节是标志位
    const start = at + key.length + (header === 0x58 ? 2 : header === 0x59 ? 3 : 1);
    const flags = bytes[start + 32] ?? 0;
    return { eligible: (flags & 0x08) !== 0, backedUp: (flags & 0x10) !== 0 };
  } catch {
    return { eligible: false, backedUp: false };
  }
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
    passkeysAvailable: MOCK_PASSKEYS_AVAILABLE,
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
//#if (Email)

/** 收得到找回码的账号：邮箱已验证、启用、没有被管理员锁定（管理员锁定没有截止时间）。 */
function isPasswordResetDeliverable(user: MockUser): boolean {
  // Mock 数据里 lockoutEnd 为 null 即管理员锁定，undefined 为未锁定；
  // 与后端一致：第二步只开了邮箱验证码的账号不能自助找回（找回后第二步只认独立因子，它没有）
  return (
    user.isEmailVerified &&
    user.isActive &&
    user.lockoutEnd !== null &&
    !(user.emailTwoFactorEnabled === true && user.twoFactorEnabled !== true)
  );
}

/**
 * 与后端同一口径：不论邮箱是否对应账号都签发挑战、占用配额，响应一样；
 * 只有可投递的账号才“收得到”验证码（Mock 下固定为 EMAIL_CODE）。
 */
function sendPasswordResetCode(req: MockRequest): EmailVerificationChallengeOutputDto {
  const body = req.body as SendEmailCodeInputDto;
  validateCaptcha(body.captchaToken, body.captchaCode);

  const email = normalizeEmail(body.email);
  const scope = getRequestScope(req);
  const rateKey = `${scope}:${email}`;
  const now = Date.now();
  const window = passwordResetWindows.get(rateKey);
  const activeWindow = window && window.startedAt + 24 * 3600 * 1000 > now ? window : undefined;
  if (
    (emailRateLimitStore.get(rateKey) ?? 0) > now ||
    (activeWindow?.count ?? 0) >= PASSWORD_RESET_DAILY_LIMIT
  ) {
    throw new MockException(429, {
      code: 'Auth:EmailCodeSendTooFrequent',
      message: 'Verification codes are being sent too frequently',
    });
  }
  passwordResetWindows.set(rateKey, {
    startedAt: activeWindow?.startedAt ?? now,
    count: (activeWindow?.count ?? 0) + 1,
  });

  const user = USERS.find((u) => u.email === body.email.trim());
  const target = user && isPasswordResetDeliverable(user) ? user : undefined;
  const challengeId = crypto.randomUUID();
  emailRateLimitStore.set(rateKey, now + EMAIL_CODE_RETRY_SECONDS * 1000);
  emailChallengeStore.set(challengeId, {
    scope,
    purpose: 'password-reset',
    email,
    // 收不到的挑战没有人知道码：用一个永远对不上的值占位
    code: target ? EMAIL_CODE : crypto.randomUUID(),
    expiresAt: now + EMAIL_CODE_EXPIRY_SECONDS * 1000,
    remainingAttempts: EMAIL_CODE_MAX_ATTEMPTS,
    userId: target?.id,
    securityVersion: target ? (target.securityVersion ?? 0) : undefined,
  });

  return {
    challengeId,
    expiresInSeconds: EMAIL_CODE_EXPIRY_SECONDS,
    retryAfterSeconds: EMAIL_CODE_RETRY_SECONDS,
  };
}

/** 码对之后还要核对目标：仍是同一个用户、安全版本未变（含别的找回挑战已经改过）、仍可投递，否则一律按码无效拒绝。 */
function resetPassword(req: MockRequest): 'ok' {
  const body = req.body as ResetPasswordInputDto;
  const email = normalizeEmail(body.email);
  const challenge = emailChallengeStore.get(body.challengeId);
  if (
    !challenge ||
    challenge.scope !== getRequestScope(req) ||
    challenge.purpose !== 'password-reset' ||
    challenge.email !== email ||
    challenge.expiresAt <= Date.now()
  ) {
    throw invalidEmailChallenge();
  }

  if (challenge.code !== body.code?.trim()) {
    challenge.remainingAttempts--;
    if (challenge.remainingAttempts <= 0) {
      emailChallengeStore.delete(body.challengeId);
    }
    throw invalidEmailChallenge();
  }

  ensureAcceptablePassword(body.newPassword, 'NewPassword');
  emailChallengeStore.delete(body.challengeId);
  const user = USERS.find((u) => u.id === challenge.userId);
  if (
    !user ||
    (user.securityVersion ?? 0) !== challenge.securityVersion ||
    normalizeEmail(user.email) !== email ||
    !isPasswordResetDeliverable(user)
  ) {
    throw invalidEmailChallenge();
  }

  user.password = body.newPassword;
  // 与后端一致：之后的登录第二步只认独立因子，直到用它完成一次
  user.passwordSetByEmailRecovery = true;
  rotateSecurityVersion(user);
  // 与后端一致：解除登录失败触发的临时锁定（有截止时间的那种）；管理员锁定在前面已经拒绝
  if (user.lockoutEnd) {
    user.lockoutEnd = undefined;
  }
  return 'ok';
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

/**
 * Mock 没有提供商往返：从 Google 回来的是只经外部登录建立的 octo（没有口令、已开两步验证，要做第二步），
 * 其余提供商回来的是第一个演示账号。
 */
function externalLoginCallback(req: MockRequest): SessionLoginOutputDto {
  const user =
    (req.params.provider === 'google' ? USERS.find((u) => u.id === 'user_octo') : undefined) ??
    USERS[0];
  if (twoFactorOptions(user, getRequestScope(req)).defaultMethod) {
    return beginTwoFactorChallenge(user, getRequestScope(req));
  }
  setMockSessionUserId(user.id);
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
  const body = (req.body ?? {}) as CompleteExternalLinkInputDto;
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
  // 确认身份不通过时票据不消费，带上凭证重试同一个请求即可
  ensureMockReauthentication(user, body);
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

/**
 * 解绑：不存在的绑定幂等成功；没有口令时不能解绑最后一种登录方式（409）；真正删除之前确认身份，
 * 会话不够新时凭证随请求体出示。
 */
function unlinkExternalLogin(req: MockRequest): 'ok' {
  const user = requireCurrentMockUser();
  const link = mockExternalLinks.find((l) => l.id === String(req.params.id));
  if (!link) {
    return 'ok';
  }
  if (!user.password && mockExternalLinks.length <= 1) {
    throw new MockException(409, {
      code: 'ExternalAuth:LastSignInMethod',
      message: 'This is your only way to sign in. Set a password before unlinking it.',
    });
  }
  ensureMockReauthentication(user, (req.body ?? {}) as UnlinkExternalLoginInputDto);
  mockExternalLinks = mockExternalLinks.filter((l) => l.id !== link.id);
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
  //#if (Email)
  'POST /api/v1/auth/password-reset/send-code': (req: MockRequest) => sendPasswordResetCode(req),
  'POST /api/v1/auth/password-reset': (req: MockRequest) => resetPassword(req),
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
  'POST /api/v1/auth/two-factor/passkey-options': (req: MockRequest) => beginTwoFactorPasskey(req),
  'POST /api/v1/auth/passkey-login/options': (req: MockRequest) => beginPasskeySignIn(req),
  'POST /api/v1/auth/passkey-login': (req: MockRequest) => completePasskeySignIn(req),
  //#if (Email)
  'POST /api/v1/auth/two-factor/email-code': (req: MockRequest) => sendTwoFactorEmailCode(req),
  //#endif
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
  'POST /api/v1/auth/me/two-factor/passkeys/options': (req: MockRequest) =>
    beginPasskeyRegistration(req),
  'POST /api/v1/auth/me/two-factor/passkeys': (req: MockRequest) =>
    completePasskeyRegistration(req),
  'PUT /api/v1/auth/me/two-factor/passkeys/:id/name': (req: MockRequest) => renamePasskey(req),
  'POST /api/v1/auth/me/two-factor/passkeys/:id/remove': (req: MockRequest) => removePasskey(req),
  'POST /api/v1/auth/me/reauthentication/passkey-options': () => beginPasskeyReauthentication(),
  'PUT /api/v1/auth/me/two-factor/preferred-method': (req: MockRequest) =>
    setPreferredTwoFactorMethod(req),
  //#if (Email)
  'POST /api/v1/auth/me/two-factor/email/setup': (req: MockRequest) =>
    beginEmailTwoFactorSetup(req),
  'POST /api/v1/auth/me/two-factor/email/enable': (req: MockRequest) => enableEmailTwoFactor(req),
  'POST /api/v1/auth/me/two-factor/email/disable': (req: MockRequest) => disableEmailTwoFactor(req),
  //#endif
  'GET /api/v1/auth/me/sessions': () => getSessions(),
  'DELETE /api/v1/auth/me/sessions/:id': (req: MockRequest) => revokeSession(req),
  'POST /api/v1/auth/me/sessions/revoke-others': () => revokeOtherSessions(),
  'POST /api/v1/auth/change-password': (req: MockRequest) => changePassword(req),
  //#if (ExternalLogin)
  'POST /api/v1/auth/me/password': (req: MockRequest) => setPassword(req),
  //#endif
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
