/**
 * 一项指标：本区间的值与紧邻的上一个等长区间的值。没有可比口径时不下发 previous（服务端省略 null）；
 * 上一区间没有数据时为 0。
 */
export interface StatisticValueDto {
  value: number;
  previous?: number | null;
}
//#if (IncludeOperationRecords && LocalIdentity)

/** 登录趋势的一天（UTC 日），按日期升序，无记录的日子为 0。 */
export interface LoginTrendPointDto {
  /** `yyyy-MM-dd`。 */
  date: string;
  succeeded: number;
  failed: number;
}
//#endif

//#if (LocalIdentity)
/** 需要管理员处理的事项计数，按当前作用域统计。 */
export interface AttentionCountsDto {
  lockedUsers: number;
  adminsWithoutTwoFactor: number;
  unconfirmedEmailUsers: number;
  //#if (LocalIdentity && IncludeMultiTenancy)
  /** 只在宿主上下文下发，租户上下文没有这一项。 */
  disabledTenants?: number | null;
  //#endif
}
//#else
/** 锁定、两步验证与邮箱都是本地身份的数据，资源服务形态下这里是空对象。 */
export type AttentionCountsDto = Record<string, never>;
//#endif

/** `GET /api/v1/platform/statistics` 的响应，统计当前作用域（宿主或本租户）。 */
export interface PlatformStatisticsOutputDto {
  days: StatisticsDays;
  totalUsers: StatisticValueDto;
  newUsers: StatisticValueDto;
  /** 资源服务形态没有登录时间，不下发。 */
  activeUsers?: StatisticValueDto;
  //#if (IncludeOperationRecords && LocalIdentity)
  /** 匿名登录失败按节流采样写入，是近似值。 */
  failedLogins: StatisticValueDto;
  loginTrend: LoginTrendPointDto[];
  //#endif
  attention: AttentionCountsDto;
}

/** 统计区间只有 7 天与 30 天两档。 */
export type StatisticsDays = 7 | 30;
