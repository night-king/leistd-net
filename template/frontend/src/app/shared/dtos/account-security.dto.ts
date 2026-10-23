import { TwoFactorMethod } from './auth.dto';

// 账户安全的只读契约：个人设置的安全面板与工作台的安全检查、登录设备共用；注册摘要由登录页、注册页与首页共用。

/** 当前租户的注册摘要（匿名可读）。 */
export interface SecurityConfigOutputDto {
  /** 是否开放自助注册；关闭时隐藏注册入口，注册页给出说明。 */
  registrationEnabled: boolean;
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
  recoveryCodesLeft: number;
  /** 所在租户要求两步验证（此时不能停用身份验证器；邮箱验证码不算满足）。 */
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
