export class User {
  id!: string;
  /** 所属租户；宿主用户为空。 */
  tenantId?: string | null;
  username!: string;
  email!: string;
  displayName?: string;
  avatar?: string;
  phoneNumber?: string;
  /** 当前邮箱是否已验证；只有本地身份的形态下有这项。 */
  isEmailVerified?: boolean;
  isTwoFactorEnabled?: boolean;
  /** 是否设有本地口令；只有本地身份的形态下有这项（只经外部登录建立的账号没有口令）。 */
  hasPassword?: boolean;
  /** 是否登记了通行密钥；只有本地身份的形态下有这项。 */
  hasPasskeys?: boolean;
  /** 受限会话：所在租户要求两步验证而本人尚未启用。只有本地身份的形态下有这项。 */
  twoFactorSetupRequired?: boolean;
  roles!: string[];
  isSuperAdmin!: boolean;

  constructor(data: Partial<User>) {
    Object.assign(this, data);
  }
}
