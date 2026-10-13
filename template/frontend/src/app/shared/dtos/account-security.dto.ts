// 账户安全的只读契约：个人设置的安全面板与工作台的安全检查、登录设备共用。

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
