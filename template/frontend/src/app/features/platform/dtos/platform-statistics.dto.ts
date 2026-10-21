/**
 * 一项指标：本区间的值、上一区间的值与按桶序列。没有可比口径时 previous 为 null 或不下发；
 * 上一区间没有数据时为 0。series 与响应的 `buckets` 等长，没有按桶来源时为 null 或不下发。
 */
export interface StatisticValueDto {
  value: number;
  previous?: number | null;
  series?: number[] | null;
}

/** 统计桶的粒度：单日按小时，其余按日。 */
export type StatisticsGranularity = 'hour' | 'day';

/** 本次统计实际采用的区间：请求的日历区间、解释它的时区，以及换算出的 UTC 范围。 */
export interface StatisticsRangeDto {
  /** `yyyy-MM-dd`，回显请求。 */
  startDate: string;
  /** `yyyy-MM-dd`，回显请求。 */
  endDate: string;
  /** 切分统计桶所用的时区（IANA 名）；坐标轴与提示按它格式化。 */
  timeZone: string;
  granularity: StatisticsGranularity;
  /** 本区间起点（含），UTC ISO 串。 */
  start: string;
  /** 本区间终点（不含），UTC ISO 串；区间含今天时是此刻。 */
  end: string;
  previousStart: string;
  previousEnd: string;
}

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
  range: StatisticsRangeDto;
  /** 各桶起点的 UTC ISO 串，升序；只含已经发生的时段，零长度区间为空。 */
  buckets: string[];
  /** 期末存量：序列是各桶终点前的存量，不能按周相加。 */
  totalUsers: StatisticValueDto;
  newUsers: StatisticValueDto;
  /** 资源服务形态没有登录时间，不下发；没有操作记录时只有本期值。 */
  activeUsers?: StatisticValueDto;
  //#if (IncludeOperationRecords && LocalIdentity)
  succeededLogins: StatisticValueDto;
  /** 失败登录告警条数：同一标识的连续失败按阈值节流记录，不是失败次数。 */
  failedLoginAlerts: StatisticValueDto;
  //#endif
  attention: AttentionCountsDto;
}

/** 统计请求：日历区间与解释它的时区。 */
export interface StatisticsQuery {
  /** `yyyy-MM-dd`。 */
  startDate: string;
  /** `yyyy-MM-dd`。 */
  endDate: string;
  /** 实际生效的展示时区（IANA 名），见 `resolveDisplayTimeZone`。 */
  timeZone: string;
}
