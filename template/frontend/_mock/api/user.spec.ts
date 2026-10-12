import { USER_API, addUser, getUsers, updateUser } from './user';
import { MockException } from '../core/models';
import { DELETED_USERS, MockUser, USERS } from '../data/user';
import { setMockSessionUserId } from '../utils/current-user';

type Handler = (req: { params: { id: string } }) => unknown;

function rejectionOf(run: () => unknown): MockException {
  try {
    run();
  } catch (error) {
    expect(error).toBeInstanceOf(MockException);
    return error as MockException;
  }
  throw new Error('expected the handler to reject');
}

/** 用户 Mock 的删除与两步验证重置，状态码与后端一致。 */
describe('user mock', () => {
  let snapshot: MockUser[];

  beforeEach(() => {
    snapshot = USERS.map((user) => ({ ...user }));
  });

  afterEach(() => {
    USERS.length = 0;
    USERS.push(...snapshot);
    DELETED_USERS.length = 0;
  });

  it('orders multiple user keys and validates sorting before returning an empty result', () => {
    USERS.forEach((user) => (user.email = 'same@example.test'));
    const result = getUsers({ sorting: 'EMAIL ascending, username descending' });
    expect(result.items.map((user) => user.username)).toEqual(['demo', 'admin']);

    const error = rejectionOf(() => getUsers({ keyword: 'missing', sorting: 'unknownProperty' }));
    expect(error.status).toBe(400);
    expect(error.error.code).toBeUndefined();
    expect(error.error.errors.map((item: { field: string }) => item.field)).toEqual(['sorting']);
  });

  //#if (LocalIdentity)
  it('sorts the nested last-login API path and places absent timestamps last in ascending order', () => {
    USERS[0].lastLoginTime = undefined;
    const result = getUsers({ sorting: 'lastLogin.Time asc' });

    expect(result.items.map((user) => user.username)).toEqual(['demo', 'admin']);
  });
  //#endif

  describe('delete', () => {
    const remove = USER_API['DELETE /api/v1/users/:id'] as Handler;

    // 删除幂等：重试或并发删除不应报错，前端也不该依赖 404 判断"已删除"
    it('succeeds for a missing user and leaves the others untouched', () => {
      expect(() => remove({ params: { id: 'user_missing' } })).not.toThrow();

      expect(USERS.map((user) => user.id)).toEqual(snapshot.map((user) => user.id));
    });

    it('removes the user, and a repeated delete still succeeds', () => {
      remove({ params: { id: 'user_demo' } });
      expect(USERS.some((user) => user.id === 'user_demo')).toBe(false);

      expect(() => remove({ params: { id: 'user_demo' } })).not.toThrow();
    });

    // 软删除：后端查重关掉了软删除过滤，删掉的用户仍占着用户名与邮箱
    it('keeps the deleted user out of the list while its username and email stay taken', () => {
      const demo = USERS.find((user) => user.id === 'user_demo')!;
      const password = 'Spec@1234567890';

      remove({ params: { id: 'user_demo' } });

      expect(getUsers({}).items.some((item) => item.id === 'user_demo')).toBe(false);
      const username = rejectionOf(() =>
        addUser({ username: demo.username, email: 'fresh@example.com', password }),
      );
      expect(username.status).toBe(409);
      expect(username.error.code).toBe('User:UsernameTaken');
      const email = rejectionOf(() => addUser({ username: 'fresh', email: demo.email, password }));
      expect(email.status).toBe(409);
      expect(email.error.code).toBe('User:EmailTaken');
      const changed = rejectionOf(() => updateUser('user_admin', { email: demo.email }));
      expect(changed.status).toBe(409);
      expect(changed.error.code).toBe('User:EmailTaken');
      expect(USERS.map((user) => user.id)).toEqual(['user_admin']);
      expect(USERS[0].email).toBe(snapshot[0].email);
    });

    it('refuses to delete the built-in super administrator with 403', () => {
      const error = rejectionOf(() => remove({ params: { id: 'user_admin' } }));

      expect(error.status).toBe(403);
      expect(error.error.code).toBe('User:SuperAdminDeleteForbidden');
      expect(USERS.some((user) => user.id === 'user_admin')).toBe(true);
    });

    it('refuses to delete the signed-in user with 400', () => {
      setMockSessionUserId('user_demo');
      onTestFinished(() => setMockSessionUserId(null));

      const error = rejectionOf(() => remove({ params: { id: 'user_demo' } }));

      expect(error.status).toBe(400);
      expect(error.error.code).toBe('User:SelfDeleteForbidden');
      expect(USERS.some((user) => user.id === 'user_demo')).toBe(true);
    });
  });

  describe('disable', () => {
    const disable = USER_API['PATCH /api/v1/users/:id/disable'] as Handler;

    afterEach(() => setMockSessionUserId(null));

    it('refuses to disable the signed-in user with 400 and keeps the account active', () => {
      setMockSessionUserId('user_demo');

      const error = rejectionOf(() => disable({ params: { id: 'user_demo' } }));

      expect(error.status).toBe(400);
      expect(error.error.code).toBe('User:SelfDisableForbidden');
      expect(USERS.find((user) => user.id === 'user_demo')!.isActive).toBe(true);
    });

    it('lets another administrator disable the user', () => {
      setMockSessionUserId('user_admin');

      disable({ params: { id: 'user_demo' } });

      expect(USERS.find((user) => user.id === 'user_demo')!.isActive).toBe(false);
    });
  });
  //#if (LocalIdentity)

  describe('reset two-factor', () => {
    const reset = USER_API['POST /api/v1/users/:id/reset-two-factor'] as Handler;

    afterEach(() => setMockSessionUserId(null));

    function enableTwoFactor(id: string): MockUser {
      const user = USERS.find((candidate) => candidate.id === id)!;
      user.twoFactorEnabled = true;
      user.recoveryCodes = ['mock-recovery-1'];
      return user;
    }

    it('returns 404 for a missing user', () => {
      const error = rejectionOf(() => reset({ params: { id: 'user_missing' } }));

      expect(error.status).toBe(404);
      expect(error.error.code).toBe('User:NotFound');
    });

    it('refuses to reset the super administrator for another administrator with 403', () => {
      setMockSessionUserId('user_demo');
      const admin = enableTwoFactor('user_admin');

      const error = rejectionOf(() => reset({ params: { id: 'user_admin' } }));

      expect(error.status).toBe(403);
      expect(error.error.code).toBe('User:SuperAdminOperationForbidden');
      expect(admin.twoFactorEnabled).toBe(true);
    });

    it('turns two-factor off and drops the recovery codes', () => {
      setMockSessionUserId('user_admin');
      const demo = enableTwoFactor('user_demo');

      reset({ params: { id: 'user_demo' } });

      expect(demo.twoFactorEnabled).toBe(false);
      expect(demo.recoveryCodes).toEqual([]);
    });
  });
  //#endif
});
