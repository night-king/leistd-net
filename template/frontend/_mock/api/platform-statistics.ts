import { requirePermission } from './authorization';
import { PERMISSIONS } from '../../src/app/shared/constants/permission.constants';
import { MockException, MockRequest } from '../core/models';
//#if (IncludeOperationRecords && LocalIdentity)
import { loginActivity } from '../data/platform-statistics';
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

const DAY = 86_400_000;

/** 今天（UTC）零点往前 `daysAgo` 天的时间戳。 */
function startOfUtcDay(daysAgo: number): number {
  const now = new Date();
  return Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate()) - daysAgo * DAY;
}

/**
 * 平台统计：总数与新注册按 Mock 用户计算，登录趋势取样本（见 data/platform-statistics.ts）。
 * 区间与后端一致：含今天在内往前 days 个 UTC 日，上一区间紧邻其前、等长。
 */
export function getPlatformStatistics(params: Record<string, unknown>) {
  requirePermission(PERMISSIONS.dashboard.default);

  // 与后端入参校验同形：省略时为 7；其他值是 400 字段错误、不带业务码
  const days = params['days'] === undefined ? 7 : Number(params['days']);
  if (days !== 7 && days !== 30) {
    throw new MockException(400, { errors: [{ field: 'days', detail: 'days must be 7 or 30.' }] });
  }

  const start = startOfUtcDay(days - 1);
  const previousStart = start - days * DAY;
  const createdAt = (user: (typeof USERS)[number]) => Date.parse(user.creationTime);
  // 与后端口径一致：新注册按创建事件计（之后删了也算）；历史总数是那一刻存在的用户，
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
  const sum = (from: number, to: number, pick: (day: ReturnType<typeof loginActivity>) => number) =>
    Array.from({ length: to - from }, (_, i) => pick(loginActivity(from + i))).reduce(
      (a, b) => a + b,
      0,
    );
  //#endif

  return {
    days,
    totalUsers: { value: USERS.length, previous: existedAt(start) },
    newUsers: {
      value: createdBetween(start, Number.POSITIVE_INFINITY),
      previous: createdBetween(previousStart, start),
    },
    //#if (IncludeOperationRecords && LocalIdentity)
    // 活跃用户是去重人数：样本按启用中的演示账号都登录过计，不超过用户总数
    activeUsers: {
      value: USERS.filter((user) => user.isActive).length,
      previous: USERS.filter((user) => user.isActive).length,
    },
    failedLogins: {
      value: sum(0, days, (day) => day.failed),
      previous: sum(days, days * 2, (day) => day.failed),
    },
    loginTrend: Array.from({ length: days }, (_, i) => {
      const daysAgo = days - 1 - i;
      const { succeeded, failed } = loginActivity(daysAgo);
      return {
        date: new Date(startOfUtcDay(daysAgo)).toISOString().slice(0, 10),
        succeeded,
        failed,
      };
    }),
    //#elseif (LocalIdentity)
    // 没有操作记录时按最近登录时间计，没有可比口径，与后端一样不下发 previous
    activeUsers: {
      value: USERS.filter((user) => user.lastLoginTime && Date.parse(user.lastLoginTime) >= start)
        .length,
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
