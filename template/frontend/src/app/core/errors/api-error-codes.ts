/**
 * 前端需要按值分支的后端错误码，与后端常量（`AuthErrorCodes`、`SecurityErrorCodes`、
 * `PermissionErrorCodes`）逐字一致；只用于展示的码不收。
 */
export const API_ERROR_CODES = {
  /** 保存权限授予时版本已过期（他人抢先保存）：重新加载，不静默覆盖。 */
  permissionConcurrencyConflict: 'Permission:ConcurrencyConflict',
  //#if (LocalIdentity)

  /** 连续认证失败触发的临时锁定：与会话失效共用 401，但会话仍有效，拦截器须据此放行。 */
  userTemporarilyLockedOut: 'Auth:UserTemporarilyLockedOut',

  /** 组织要求两步验证而本人尚未启用：带去设置页。 */
  twoFactorSetupRequired: 'Auth:TwoFactorSetupRequired',

  /** 两步验证码不正确：停在当前页重试，不退出挑战流程。 */
  twoFactorCodeInvalid: 'Auth:TwoFactorCodeInvalid',

  /** 会话不够新，要当场出示本人的当前口令或身份验证器上的验证码。 */
  reauthenticationRequired: 'Auth:ReauthenticationRequired',

  /** 再认证时出示的当前口令不正确：留在输入口令的地方重输。 */
  currentPasswordIncorrect: 'Security:CurrentPasswordIncorrect',

  /** 会话不够新，而账号既没有本地口令也没开两步验证：重新登录才能继续。 */
  signInAgainRequired: 'Auth:SignInAgainRequired',

  /** 出示的确认方式本账号不具备（账号状态已变）：刷新当前用户，按新状态重新给出可选方式。 */
  reauthenticationMethodUnavailable: 'Auth:ReauthenticationMethodUnavailable',

  /** 这一次两步验证设置已失效（过期、别处重新开始或凭据变过）：清掉二维码与密钥，请本人重新开始。 */
  twoFactorSetupExpired: 'Auth:TwoFactorSetupExpired',
  //#endif
} as const;
