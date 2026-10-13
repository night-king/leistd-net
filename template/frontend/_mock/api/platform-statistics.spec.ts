import { getPlatformStatistics } from './platform-statistics';
//#if (LocalIdentity)
import { deleteUser } from './user';
//#endif
import { MockException } from '../core/models';
//#if (LocalIdentity)
import { DELETED_USERS, USERS } from '../data/user';
//#endif
import { setMockSessionUserId } from '../utils/current-user';

describe('platform statistics mock', () => {
  afterEach(() => setMockSessionUserId(null));

  function statusOf(action: () => unknown): number | undefined {
    try {
      action();
    } catch (error) {
      return (error as MockException).status;
    }
    return undefined;
  }

  it('requires the dashboard permission', () => {
    setMockSessionUserId('user_demo');

    expect(statusOf(() => getPlatformStatistics({ days: '7' }))).toBe(403);
  });

  it('accepts only 7 or 30 days', () => {
    setMockSessionUserId('user_admin');

    expect(statusOf(() => getPlatformStatistics({ days: '14' }))).toBe(400);
  });
  //#if (LocalIdentity)

  it('keeps deleted users in the earlier total and in sign-ups', () => {
    setMockSessionUserId('user_admin');
    const snapshot = [...USERS];
    onTestFinished(() => {
      USERS.splice(0, USERS.length, ...snapshot);
      DELETED_USERS.length = 0;
    });
    const before = getPlatformStatistics({ days: '7' });

    deleteUser('user_demo');
    const after = getPlatformStatistics({ days: '7' });

    expect(after.totalUsers).toEqual({
      value: before.totalUsers.value - 1,
      previous: before.totalUsers.previous,
    });
    expect(after.newUsers).toEqual(before.newUsers);
  });
  //#endif

  it('defaults to 7 days when the range is omitted', () => {
    setMockSessionUserId('user_admin');

    expect(getPlatformStatistics({}).days).toBe(7);
  });
  //#if (IncludeOperationRecords && LocalIdentity)

  it('returns one trend point per UTC day in ascending order, ending today', () => {
    setMockSessionUserId('user_admin');

    const { loginTrend } = getPlatformStatistics({ days: '30' });

    expect(loginTrend).toHaveLength(30);
    expect(loginTrend.at(-1)?.date).toBe(new Date().toISOString().slice(0, 10));
    expect([...loginTrend].map((point) => point.date).sort()).toEqual(
      loginTrend.map((point) => point.date),
    );
  });
  //#endif
});
