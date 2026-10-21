import { requirePermission } from './authorization';
import { PERMISSIONS } from '../../src/app/shared/constants/permission.constants';
import { MockException, MockRequest } from '../core/models';
//#if (IncludeOperationRecords && LocalIdentity)
import { MockLoginSlot, sumLoginSlots } from '../data/platform-statistics';
//#endif
//#if (LocalIdentity && IncludeMultiTenancy)
import { TENANTS } from '../data/tenant';
//#endif
//#if (LocalIdentity)
import { DELETED_USERS, USERS } from '../data/user';
//#else
import { USERS } from '../data/user';
//#endif
//#if (LocalIdentity && IncludeMultiTenancy)
import { getMockSessionTenantKey } from '../utils/current-user';
//#endif
import {
  addDays,
  daysBetween,
  isUsableTimeZone,
  resolvePeriod,
  todayIn,
} from '../utils/statistics-calendar';

/** 区间最多包含的日数，与后端一致。 */
const MAX_DAYS = 366;

interface StatisticsQuery {
  startDate: string;
  endDate: string;
  timeZone: string;
}

/** 与后端同形的字段错误（400，不带业务码）。 */
function fieldErrors(errors: { field: string; detail: string }[]): MockException {
  return new MockException(400, { errors });
}

/**
 * 与后端入参校验一致：三个参数必填；日期按 `YYYY-MM-DD`；时区须能被 `Intl` 使用；
 * 起止不能颠倒、最多 366 天、结束日不能晚于该时区的今天。
 */
function parseQuery(params: Record<string, unknown>, now: number): StatisticsQuery {
  // 拦截器按 getAll 给出数组，单测直接传字符串：两种都取第一个值
  const text = (name: string) => {
    const value = Array.isArray(params[name]) ? (params[name] as unknown[])[0] : params[name];
    return typeof value === 'string' ? value : '';
  };
  const isDate = (value: string) =>
    /^\d{4}-\d{2}-\d{2}$/.test(value) &&
    !Number.isNaN(Date.parse(`${value}T00:00:00Z`)) &&
    new Date(`${value}T00:00:00Z`).toISOString().startsWith(value);
  const errors: { field: string; detail: string }[] = [];
  for (const field of ['startDate', 'endDate'] as const) {
    if (!text(field)) {
      errors.push({ field, detail: `${field} is required.` });
    } else if (!isDate(text(field))) {
      errors.push({ field, detail: `${field} is not a valid date.` });
    }
  }
  if (!text('timeZone')) {
    errors.push({ field: 'timeZone', detail: 'timeZone is required.' });
  }
  if (errors.length > 0) {
    throw fieldErrors(errors);
  }

  const query = {
    startDate: text('startDate'),
    endDate: text('endDate'),
    timeZone: text('timeZone'),
  };
  const days = daysBetween(query.startDate, query.endDate) + 1;
  if (days < 1) {
    throw fieldErrors([
      { field: 'endDate', detail: 'The end date cannot be earlier than the start date.' },
    ]);
  }
  if (days > MAX_DAYS) {
    throw fieldErrors([{ field: 'endDate', detail: 'The date range cannot exceed 366 days.' }]);
  }
  // 与后端一致：上一区间的起点（再算上时区偏移）要落在可表示的日期范围内
  if (addDays(query.startDate, -days) < '0001-01-02') {
    throw fieldErrors([{ field: 'startDate', detail: 'The start date is too early.' }]);
  }
  if (!isUsableTimeZone(query.timeZone)) {
    throw fieldErrors([
      { field: 'timeZone', detail: 'The time zone is not a valid IANA time zone.' },
    ]);
  }
  if (query.endDate > todayIn(query.timeZone, now)) {
    throw fieldErrors([{ field: 'endDate', detail: 'The end date cannot be later than today.' }]);
  }
  return query;
}

/**
 * 平台统计：用户总数与新注册按 Mock 用户计算，登录次数取样本（见 data/platform-statistics.ts）。
 * 区间、上一区间与桶边界和后端同一规则（见 utils/statistics-calendar.ts）。
 */
export function getPlatformStatistics(params: Record<string, unknown>, now: number = Date.now()) {
  requirePermission(PERMISSIONS.dashboard.default);

  const query = parseQuery(params, now);
  const period = resolvePeriod(query.startDate, query.endDate, query.timeZone, now);
  const buckets = period.edges.slice(0, -1);
  const eachBucket = (count: (from: number, to: number) => number) =>
    buckets.map((from, index) => count(from, period.edges[index + 1]));

  const createdAt = (user: (typeof USERS)[number]) => Date.parse(user.creationTime);
  // 与后端口径一致：新注册按创建事件计（之后删了也算）；某一时刻的总数是那一刻存在的用户，
  // 即在其前创建、且当时尚未删除的
  //#if (LocalIdentity)
  const everCreated = [...USERS, ...DELETED_USERS];
  //#else
  // 资源服务形态没有删除入口，用户行只增不减
  const everCreated = USERS;
  //#endif
  const existedAt = (time: number) =>
    everCreated.filter(
      (user) =>
        createdAt(user) < time && (!user.deletionTime || Date.parse(user.deletionTime) >= time),
    ).length;
  const createdBetween = (from: number, to: number) =>
    everCreated.filter((user) => createdAt(user) >= from && createdAt(user) < to).length;
  //#if (IncludeOperationRecords && LocalIdentity)
  const logins = (pick: (slot: MockLoginSlot) => number) => {
    const series = eachBucket((from, to) => sumLoginSlots(from, to, pick));
    return {
      value: series.reduce((sum, count) => sum + count, 0),
      previous: sumLoginSlots(period.previousStart, period.previousEnd, pick),
      series,
    };
  };
  // 活跃用户是去重人数：样本按启用中的演示账号轮流登录计，不超过它们的人数
  const activeCount = USERS.filter((user) => user.isActive).length;
  const distinctUsers = (from: number, to: number) =>
    Math.min(
      activeCount,
      sumLoginSlots(from, to, (slot) => slot.succeeded),
    );
  //#endif

  return {
    range: {
      startDate: query.startDate,
      endDate: query.endDate,
      timeZone: query.timeZone,
      granularity: period.granularity,
      start: new Date(period.start).toISOString(),
      end: new Date(period.end).toISOString(),
      previousStart: new Date(period.previousStart).toISOString(),
      previousEnd: new Date(period.previousEnd).toISOString(),
    },
    buckets: buckets.map((edge) => new Date(edge).toISOString()),
    totalUsers: {
      value: existedAt(period.end),
      previous: existedAt(period.previousEnd),
      series: eachBucket((_, to) => existedAt(to)),
    },
    newUsers: {
      value: createdBetween(period.start, period.end),
      previous: createdBetween(period.previousStart, period.previousEnd),
      series: eachBucket(createdBetween),
    },
    //#if (IncludeOperationRecords && LocalIdentity)
    activeUsers: {
      value: distinctUsers(period.start, period.end),
      previous: distinctUsers(period.previousStart, period.previousEnd),
      series: eachBucket(distinctUsers),
    },
    succeededLogins: logins((slot) => slot.succeeded),
    failedLoginAlerts: logins((slot) => slot.failed),
    //#elseif (LocalIdentity)
    // 没有操作记录时按最近登录时间计：只有本期值，与后端一样不下发 previous 与 series
    activeUsers: {
      value: USERS.filter(
        (user) =>
          user.lastLoginTime &&
          Date.parse(user.lastLoginTime) >= period.start &&
          Date.parse(user.lastLoginTime) < period.end,
      ).length,
      previous: null,
      series: null,
    },
    //#endif
    //#if (LocalIdentity)
    attention: {
      lockedUsers: USERS.filter(
        (user) =>
          user.lockoutEnd === null ||
          (user.lockoutEnd !== undefined && Date.parse(user.lockoutEnd) > Date.now()),
      ).length,
      adminsWithoutTwoFactor: USERS.filter(
        (user) => user.roles.includes('Admin') && user.twoFactorEnabled !== true,
      ).length,
      unconfirmedEmailUsers: USERS.filter((user) => !user.isEmailVerified).length,
      //#if (IncludeMultiTenancy)
      // 只在宿主上下文下发
      ...(getMockSessionTenantKey() === 'host'
        ? { disabledTenants: TENANTS.filter((tenant) => !tenant.isActive).length }
        : {}),
      //#endif
    },
    //#else
    // 与后端一致：锁定、两步验证、邮箱都是本地身份的数据，资源服务形态下为空对象
    attention: {},
    //#endif
  };
}

export const PLATFORM_STATISTICS_API = {
  'GET /api/v1/platform/statistics': (req: MockRequest) => getPlatformStatistics(req.queryParams),
};
