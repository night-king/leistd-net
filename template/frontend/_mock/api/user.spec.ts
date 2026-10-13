import {
  USER_API,
  addUser,
  batchAssignUserRoles,
  batchDisableUsers,
  getUsers,
  updateUser,
} from './user';
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
    // 邮箱全部相同时第二个键生效：用户名降序
    expect(result.items.map((user) => user.username)).toEqual(
      USERS.map((user) => user.username).sort((a, b) => (a < b ? 1 : a > b ? -1 : 0)),
    );

    const error = rejectionOf(() => getUsers({ keyword: 'missing', sorting: 'unknownProperty' }));
    expect(error.status).toBe(400);
    expect(error.error.code).toBeUndefined();
    expect(error.error.errors.map((item: { field: string }) => item.field)).toEqual(['sorting']);
  });

  describe('batch operations', () => {
    afterEach(() => setMockSessionUserId(null));

    it('runs each item on its own and keeps the successes when some are rejected', () => {
      setMockSessionUserId('user_admin');

      const result = batchDisableUsers({ ids: ['user_demo', 'user_admin', 'user_demo'] });

      // 重复的只处理一次；停用自己（超级管理员）被拒，不影响另一条
      expect(result.items.map((item) => [item.id, item.succeeded])).toEqual([
        ['user_demo', true],
        ['user_admin', false],
      ]);
      expect(result.items[1].errorCode).toBeTruthy();
      expect(USERS.find((user) => user.id === 'user_demo')?.isActive).toBe(false);
    });

    it('rejects an empty or oversized id list with a field error', () => {
      setMockSessionUserId('user_admin');

      expect(rejectionOf(() => batchDisableUsers({ ids: [] })).status).toBe(400);
      const tooMany = Array.from({ length: 101 }, (_, i) => `u${i}`);
      expect(rejectionOf(() => batchDisableUsers({ ids: tooMany })).status).toBe(400);
      // 数量按提交的原数组算：同一个 Id 重复 101 次也超限，一条都不执行
      const repeated = Array.from({ length: 101 }, () => 'user_demo');
      expect(rejectionOf(() => batchDisableUsers({ ids: repeated })).status).toBe(400);
      expect(USERS.find((user) => user.id === 'user_demo')?.isActive).toBe(true);
      const roles = Array.from({ length: 21 }, () => 'role_admin');
      expect(
        rejectionOf(() => batchAssignUserRoles({ ids: ['user_demo'], roleIds: roles })).status,
      ).toBe(400);
    });

    it("refuses to add roles to another administrator's built-in super administrator", () => {
      // 快照是浅拷贝，换新数组而不是原地追加，用例结束后才能还原
      const demo = USERS.find((user) => user.id === 'user_demo')!;
      demo.roles = [...demo.roles, 'Admin'];
      setMockSessionUserId('user_demo');

      const result = batchAssignUserRoles({ ids: ['user_admin'], roleIds: ['role_admin'] });

      expect(result.items[0]).toMatchObject({
        id: 'user_admin',
        succeeded: false,
        errorCode: 'User:SuperAdminUpdateForbidden',
      });
    });

    it('adds roles without removing existing ones and rejects unknown roles as a whole', () => {
      setMockSessionUserId('user_admin');

      batchAssignUserRoles({ ids: ['user_demo'], roleIds: ['role_admin'] });
      expect(USERS.find((user) => user.id === 'user_demo')?.roles).toEqual(['Member', 'Admin']);

      const error = rejectionOf(() =>
        batchAssignUserRoles({ ids: ['user_demo'], roleIds: ['missing'] }),
      );
      expect(error.status).toBe(400);
      expect(error.error.code).toBe('User:RolesNotFound');
    });

    it('requires the update permission', () => {
      setMockSessionUserId('user_demo');

      expect(rejectionOf(() => batchDisableUsers({ ids: ['user_admin'] })).status).toBe(403);
    });
  });

  //#if (LocalIdentity)
  it('sorts the nested last-login API path and places absent timestamps last in ascending order', () => {
    USERS[0].lastLoginTime = undefined;
    const result = getUsers({ sorting: 'lastLogin.Time asc' });
    const withLogin = USERS.filter((user) => user.lastLoginTime)
      .sort((a, b) => Date.parse(a.lastLoginTime!) - Date.parse(b.lastLoginTime!))
      .map((user) => user.username);

    // 有时间的按升序在前，没有登录过的（含刚清空的 admin）排在最后
    const usernames = result.items.map((user) => user.username);
    expect(usernames.slice(0, withLogin.length)).toEqual(withLogin);
    expect(usernames.slice(withLogin.length)).toContain('admin');
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
      expect(USERS.map((user) => user.id)).toEqual(
        snapshot.map((user) => user.id).filter((id) => id !== 'user_demo'),
      );
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
