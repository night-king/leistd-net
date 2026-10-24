import { TwoFactorMethod } from './auth.dto';

// 账户安全的只读契约：个人设置的安全面板与工作台的安全检查、登录设备共用；注册摘要由登录页、注册页与首页共用。

/** 当前租户的注册摘要（匿名可读）。 */
export interface SecurityConfigOutputDto {
  /** 是否开放自助注册；关闭时隐藏注册入口，注册页给出说明。 */
  registrationEnabled: boolean;
  /** 部署配置了通行密钥的依赖方：为 false 时登录页不给"用通行密钥登录"与条件式自动填充。 */
  passkeysAvailable: boolean;
  //#if (Email)
  /** 注册时是否要求邮箱验证。 */
  enableEmailVerification: boolean;
  /** 部署具备发邮箱验证码的前提；为 false 时"验证我的邮箱"只说明暂不可用，不给发送按钮。 */
  emailVerificationAvailable: boolean;
  //#endif
}

export interface TwoFactorStatusOutputDto {
  /** 是否已启用身份验证器。 */
  enabled: boolean;
  /** 本人登记的通行密钥，按登记先后。 */
  passkeys: PasskeyOutputDto[];
  /** 部署配置了通行密钥的依赖方：为 false 时不能添加，已登记的也不能用于登录。 */
  passkeysAvailable: boolean;
  /** 剩余可用的恢复码个数；恢复码挂在账号上，有任一独立因子（身份验证器或通行密钥）就有。 */
  recoveryCodesLeft: number;
  /** 所在租户要求两步验证（此时不能去掉最后一个独立因子；邮箱验证码不算满足）。 */
  requiredByPolicy: boolean;
  /** 本人指定的首选方式；未指定时没有这一项。 */
  preferredMethod?: TwoFactorMethod | null;
  /** 登录第二步此刻直接给出的方式；不需要第二步时没有这一项。 */
  defaultMethod?: TwoFactorMethod | null;
  //#if (Email)
  /** 本人开启了邮箱验证码。 */
  emailEnabled?: boolean;
  /** 邮箱验证码此刻为什么用不了（开启前也按它判断能否开启）；能用时没有这一项。 */
  emailUnavailableReason?: TwoFactorEmailUnavailableReason | null;
  /** 收码地址的打码形式。 */
  maskedEmail?: string | null;
  //#endif
}

/**
 * WebAuthn 的 JSON 形式（选项或凭据）：二进制字段是 base64url 字符串。服务端按协议库的规则生成与解析，
 * 前端只在 `core/webauthn` 里与浏览器的二进制形式互转，其余地方原样传递。
 */
export type WebAuthnJson = Record<string, unknown>;

/** 交给浏览器的 WebAuthn 选项（创建或断言）。 */
export interface PasskeyOptionsOutputDto {
  options: WebAuthnJson;
  /** 免口令登录的仪式凭据，完成时原样交回；其他仪式没有这一项。 */
  ceremony?: string | null;
}

/** 本人登记的一把通行密钥。 */
export interface PasskeyOutputDto {
  id: string;
  name: string;
  /** 登记时刻（UTC）。 */
  creationTime: string;
  /** 最近一次使用（UTC）；从未使用时没有这一项。 */
  lastUsedTime?: string | null;
  /** 凭据可以被同步备份（多设备通行密钥）。 */
  isBackupEligible: boolean;
  /** 凭据当前已备份。 */
  isBackedUp: boolean;
}
//#if (Email)

/** 邮箱验证码不能作为第二步的原因：组织未允许、邮箱未验证、部署暂时发不了验证码。 */
export type TwoFactorEmailUnavailableReason =
  'notAllowed' | 'emailNotVerified' | 'deliveryUnavailable';
//#endif

/** 一个登录中的会话（登录设备）。 */
export interface UserSessionOutputDto {
  id: string;
  creationTime: string;
  /** 最近活跃时间，按分钟节流更新。 */
  lastSeenTime: string;
  ipAddress?: string | null;
  /** User-Agent 原文，界面归纳成"浏览器 · 系统"。 */
  userAgent?: string | null;
  /** 模拟登录建立的会话：发起人名称。 */
  impersonatorName?: string | null;
  isCurrent: boolean;
}
