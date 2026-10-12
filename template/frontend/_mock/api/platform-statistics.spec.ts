import { getPlatformStatistics } from './platform-statistics';
import { MockException } from '../core/models';
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
