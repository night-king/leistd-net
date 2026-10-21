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
  enabled: boolean;
  recoveryCodesLeft: number;
  /** 所在租户要求两步验证（此时不能停用）。 */
  requiredByPolicy: boolean;
}

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
