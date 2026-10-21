import { AUTHORIZATION_API, getRoleOptions, getRoles } from './authorization';
import { PERMISSIONS } from '../../src/app/shared/constants/permission.constants';
import { PERMISSION_DEFINITIONS, ROLES } from '../data/authorization';
import { setMockSessionUserId } from '../utils/current-user';

/**
 * 超管在 Mock 下拿到定义树里的全部权限，与真实后端的超管旁路一致。
 * 少一项，对应菜单就不出现、页面进 403，Mock 模式下那一页等于没法用。
 */
describe('current permissions mock', () => {
  afterEach(() => setMockSessionUserId(null));

  function currentPermissions(): string[] {
    setMockSessionUserId('user_admin');
    const result = AUTHORIZATION_API['GET /api/v1/permissions/current']() as {
      permissions: string[];
    };
    return result.permissions;
  }

  it('grants a super admin every permission in the definition tree', () => {
    const defined = PERMISSION_DEFINITIONS.flatMap((group) =>
      group.permissions.flatMap((permission) => [
        permission.name,
        ...permission.children.map((child) => child.name),
      ]),
    );

    expect(currentPermissions().sort()).toEqual(defined.sort());
  });

  it('includes every enabled platform entry', () => {
    const permissions = currentPermissions();
    expect(permissions).toContain(PERMISSIONS.users.default);
    //#if (IncludeOperationRecords)
    expect(permissions).toContain(PERMISSIONS.operationRecords.default);
    //#endif
    //#if (LocalIdentity && IncludeMultiTenancy)
    expect(permissions).toContain(PERMISSIONS.tenants.default);
    //#endif
  });
});

/** 角色选项 Mock 的排序契约。 */
describe('getRoleOptions', () => {
  const originalOrder = ROLES.map((role) => role.id);
  const temporaryIds = ['role_tmp_zeta', 'role_tmp_alpha'];

  beforeEach(() => {
    setMockSessionUserId('user_admin');

    // 并列排序号不是理论场景：createRole 缺省 sort = 0。
    // 故意按名称倒序插入，插入顺序与期望顺序相反才测得出排序真的发生了
    ROLES.push(
      {
        id: 'role_tmp_zeta',
        name: 'Zeta',
        displayName: 'Zeta',
        description: '',
        isStatic: false,
        isDefault: false,
        sort: 4242,
        creationTime: '2025-01-01T00:00:00Z',
      },
      {
        id: 'role_tmp_alpha',
        name: 'Alpha',
        displayName: 'Alpha',
        description: '',
        isStatic: false,
        isDefault: false,
        sort: 4242,
        creationTime: '2025-01-01T00:00:00Z',
      },
    );
  });

  afterEach(() => {
    for (let index = ROLES.length - 1; index >= 0; index--) {
      if (temporaryIds.includes(ROLES[index].id)) {
        ROLES.splice(index, 1);
      }
    }
    setMockSessionUserId(null);
  });

  // 与后端 GetAllAsync 同口径。
  it('sorts by name ascending when sort numbers tie', () => {
    const tied = getRoleOptions()
      .filter((option) => temporaryIds.includes(option.id))
      .map((option) => option.name);

    expect(tied).toEqual(['Alpha', 'Zeta']);
  });

  it('does not reorder the global ROLES', () => {
    getRoleOptions();

    expect(ROLES.map((role) => role.id)).toEqual([...originalOrder, ...temporaryIds]);
  });
});

// 与后端一致：删除幂等，角色不存在（含已删除）即成功，重试不报错
describe('role mock sorting', () => {
  let snapshot: typeof ROLES;

  beforeEach(() => {
    snapshot = [...ROLES];
    setMockSessionUserId('user_admin');
    ROLES.push(
      ...['Zeta', 'Alpha'].map((name) => ({
        id: `sorting_${name}`,
        name,
        displayName: name,
        sort: 4242,
        isStatic: false,
        isDefault: false,
        creationTime: '2025-01-01T00:00:00Z',
      })),
    );
  });

  afterEach(() => {
    ROLES.splice(0, ROLES.length, ...snapshot);
    setMockSessionUserId(null);
  });

  function names(sorting?: string) {
    return (getRoles({ sorting, limit: 100 }).items as { id: string; name: string }[])
      .filter((role) => role.id.startsWith('sorting_'))
      .map((role) => role.name);
  }

  it('uses the same default and explicit sort, and honors an explicit secondary direction', () => {
    expect(names()).toEqual(['Alpha', 'Zeta']);
    expect(names('sort asc')).toEqual(names());
    expect(names('SORT ascending, name descending')).toEqual(['Zeta', 'Alpha']);
  });
});

describe('deleteRole', () => {
  const remove = AUTHORIZATION_API['DELETE /api/v1/roles/:id'] as (req: {
    params: { id: string };
  }) => unknown;

  afterEach(() => setMockSessionUserId(null));

  it('succeeds for a missing role without touching the others', () => {
    setMockSessionUserId('user_admin');
    const before = ROLES.map((role) => role.id);

    expect(() => remove({ params: { id: 'role_missing' } })).not.toThrow();

    expect(ROLES.map((role) => role.id)).toEqual(before);
  });

  it('still requires the delete permission for a missing role', () => {
    setMockSessionUserId('user_demo');

    expect(() => remove({ params: { id: 'role_missing' } })).toThrow(
      expect.objectContaining({ status: 403 }),
    );
  });
});
