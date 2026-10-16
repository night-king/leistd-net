/**
 * 认证契约 DTO，放在 `shared/`：`core` 的 AuthService、account 特性与 Mock 都消费它们，
 * 留在特性目录会让 `core` 反向依赖 `features`。
 */
//#if (LocalIdentity)

export interface LoginInputDto {
  usernameOrEmail: string;
  password: string;
}

/** 登录第一步（密码或外部登录）的结果。 */
export interface SessionLoginOutputDto {
  /** 还需要第二步：凭 twoFactorToken 提交验证码或恢复码后才下发会话。 */
  requiresTwoFactor?: boolean;
  twoFactorToken?: string | null;
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
   * 是否设有本地口令；只经外部登录建立的账号没有。与 `isTwoFactorEnabled` 一起决定确认身份时能出示哪种凭证，
   * 是否需要确认仍由服务端判定。
   */
  hasPassword: boolean;
  //#endif
  /** 受限会话：所在租户要求两步验证而本人尚未启用，必须先完成设置。 */
  twoFactorSetupRequired?: boolean;
  isSuperAdmin: boolean;
  //#if (LocalIdentity)
  creationTime: string;
  //#endif
  roles: string[];
}
