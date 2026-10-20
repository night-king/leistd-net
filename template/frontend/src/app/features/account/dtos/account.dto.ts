export interface RegisterInputDto {
  username: string;
  email: string;
  password: string;
  captchaCode?: string;
  captchaToken?: string;
//#if (Email)
  emailVerification?: EmailVerificationInputDto;
//#endif
  displayName?: string;
}

/** 登录第二步：验证码与恢复码二选一。 */
export interface TwoFactorLoginInputDto {
  token: string;
  code?: string;
  recoveryCode?: string;
}

/**
 * 开始设置：会话不够新时带当前口令。换绑时带现有验证器的码（同时算确认身份，不再另带口令），
 * 或一个恢复码（它不算确认身份，会话不够新时仍要口令）。
 */
export interface BeginTwoFactorSetupInputDto {
  password?: string;
  code?: string;
  recoveryCode?: string;
}

export interface TwoFactorSetupOutputDto {
  /** 这一次设置的 Id，确认时原样带回。 */
  setupId: string;
  /** Base32 密钥，供无法扫码时手动输入。 */
  secret: string;
  /** otpauth:// 地址，二维码的内容。 */
  otpAuthUri: string;
}

export interface TwoFactorRecoveryCodesOutputDto {
  recoveryCodes: string[];
}

/** 停用两步验证：有口令的账号口令与验证码都要；没有口令的账号只带验证码。 */
export interface DisableTwoFactorInputDto {
  password?: string;
  code: string;
}

export interface CaptchaOutputDto {
  captchaToken: string;
  captchaImageBase64: string;
}
//#if (Email)

export interface SendEmailCodeInputDto {
  email: string;
  captchaToken: string;
  captchaCode: string;
}
//#endif
//#if (Email)

export interface EmailVerificationInputDto {
  challengeId: string;
  code: string;
}
//#endif
//#if (Email)

export interface EmailVerificationChallengeOutputDto {
  challengeId: string;
  expiresInSeconds: number;
  retryAfterSeconds: number;
}
//#endif
//#if (Email)

/** 忘记密码：凭发到账号邮箱的验证码设置新口令。 */
export interface ResetPasswordInputDto {
  email: string;
  challengeId: string;
  code: string;
  newPassword: string;
}
//#endif

export interface UpdateCurrentUserInputDto {
  username: string;
  email: string;
  displayName?: string;
  phoneNumber?: string;
  /** 本人的当前口令：换邮箱而会话不够新时带上，与 `twoFactorCode` 二选一。 */
  currentPassword?: string;
  /** 本人身份验证器上的 6 位验证码：换邮箱而会话不够新时带上，与 `currentPassword` 二选一。 */
  twoFactorCode?: string;
}

/** 设置自己的头像：图片的 data URL（浏览器端已裁剪缩放）；`null` 清除。 */
export interface SetAvatarInputDto {
  avatar: string | null;
}

export interface ChangePasswordInputDto {
  currentPassword: string;
  newPassword: string;
  confirmPassword: string;
}
//#if (ExternalLogin)

/** 没有本地口令的账号设置第一个口令；会话不够新时带上身份验证器上的验证码（账号没有口令可出示）。 */
export interface SetPasswordInputDto {
  newPassword: string;
  confirmPassword: string;
  twoFactorCode?: string;
}

/** 外部授权回来后完成绑定：会话不够新时带上本人的当前口令或验证码（二选一）。 */
export interface CompleteExternalLinkInputDto {
  password?: string;
  twoFactorCode?: string;
}

/** 解绑外部账号：会话不够新时带上本人的当前口令或验证码（二选一）。 */
export interface UnlinkExternalLoginInputDto {
  password?: string;
  twoFactorCode?: string;
}

/** 部署已配置的外部登录提供商。 */
export interface ExternalLoginProvidersOutputDto {
  /** 提供商标识（`github`、`google`），按名称排序。 */
  providers: string[];
}

/** 本人的外部账号绑定情况。 */
export interface ExternalLoginsOutputDto {
  /** 是否设有密码；没有时最后一个绑定不能解绑。 */
  hasPassword: boolean;
  /** 部署已配置的提供商，各附带本人的绑定。 */
  providers: ExternalLoginProviderOutputDto[];
}

export interface ExternalLoginProviderOutputDto {
  provider: string;
  /** 本人在该提供商下的绑定；未绑定时缺省。 */
  link?: ExternalLoginLinkOutputDto | null;
}

export interface ExternalLoginLinkOutputDto {
  id: string;
  providerAccountLabel?: string | null;
  providerEmail?: string | null;
  creationTime: string;
}
//#endif
//#if (OpenIddictServer)

/** 依赖方发起退出、需要用户确认时，确认页所需的信息。 */
export interface LogoutConfirmationOutputDto {
  /** 确认凭据仍然有效且属于当前会话；为 false 时其余字段为空，应提示从应用重新发起退出。 */
  isValid: boolean;
  /** 发起退出的应用名称；退出请求未标明客户端时为空。 */
  applicationName?: string | null;
  /** 确认表单须携带的官方防伪令牌字段名与令牌。 */
  antiforgeryFieldName?: string | null;
  antiforgeryToken?: string | null;
}
//#endif
