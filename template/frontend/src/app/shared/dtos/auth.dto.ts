/**
 * 认证契约 DTO，放在 `shared/`：`core` 的 AuthService、account 特性与 Mock 都消费它们，
 * 留在特性目录会让 `core` 反向依赖 `features`。
 */
import { AccountPreferencesDto } from './account-preferences.dto';
//#if (LocalIdentity)

export interface LoginInputDto {
  usernameOrEmail: string;
  password: string;
}

/**
 * 登录第二步的验证方式。默认顺序 通行密钥 → 身份验证器 → 邮箱验证码；恢复码只作备选，
 * 只有其余方式都用不了（部署没配通行密钥、本人又只有通行密钥）时才是默认方式。
 */
export type TwoFactorMethod = 'passkey' | 'authenticator' | 'email' | 'recoveryCode';

/** 登录第一步（密码或外部登录）的结果。 */
export interface SessionLoginOutputDto {
  /** 还需要第二步：凭 twoFactorToken 以其中一种方式通过后才下发会话。 */
  requiresTwoFactor?: boolean;
  twoFactorToken?: string | null;
  /** 第二步能用的方式，按系统顺序，恢复码在最后。 */
  twoFactorMethods?: TwoFactorMethod[] | null;
  /** 第二步直接给出的方式（本人首选，否则按系统顺序）。 */
  defaultTwoFactorMethod?: TwoFactorMethod | null;
  /** 邮箱验证码能用时，收码地址的打码形式。 */
  maskedEmail?: string | null;
  /** 外部登录的站内回跳地址，由服务端校验并随受保护票据保存。 */
  returnUrl?: string | null;
}
//#endif

/** 用户输出；`User` 领域模型由它构造（见 `user.model.ts`）。 */
export interface UserOutputDto {
  id: string;
  tenantId?: string | null;
  username: string;
  email: string;
  displayName?: string;
  /** 头像地址：上传的图片是带版本号的站内地址（`/api/v1/users/{id}/avatar?v=…`），外部头像是原地址。 */
  avatar?: string;
  phoneNumber?: string;
  //#if (LocalIdentity)
  isActive: boolean;
  //#endif
  /** 当前邮箱是否已验证；改过邮箱后回到未验证。 */
  isEmailVerified: boolean;
  isTwoFactorEnabled?: boolean;
  //#if (LocalIdentity)
  /**
   * 是否设有本地口令；只经外部登录建立的账号没有。与 `isTwoFactorEnabled`、`hasPasskeys` 一起决定确认身份时
   * 能出示哪种凭证，是否需要确认仍由服务端判定。
   */
  hasPassword: boolean;
  /** 是否登记了通行密钥（确认身份时能用它）。 */
  hasPasskeys: boolean;
  //#endif
  /**
   * 受限会话：所在租户要求两步验证而本人尚未启用，必须先完成设置。服务端每个请求按当时的策略与本人状态判定，
   * 这里是读取那一刻的结论：策略或状态变了要重新读当前用户，不能沿用旧值。
   */
  twoFactorSetupRequired?: boolean;
  isSuperAdmin: boolean;
  //#if (LocalIdentity)
  creationTime: string;
  //#endif
  roles: string[];
  /**
   * 账户偏好的生效值与个人覆盖原值，只在 `GET /auth/me` 上下发；其他返回当前用户的接口（改资料、头像等）
   * 不带，消费方不能把缺失当成"未设置"。
   */
  preferences?: AccountPreferencesDto | null;
  preferenceOverrides?: AccountPreferencesDto | null;
}
