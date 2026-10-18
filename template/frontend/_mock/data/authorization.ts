import { PERMISSIONS } from '../../src/app/shared/constants/permission.constants';

export interface MockRole {
  id: string;
  name: string;
  displayName: string;
  description?: string;
  isStatic: boolean;
  isDefault: boolean;
  sort: number;
  creationTime: string;
  lastModificationTime?: string;
}

export const ROLES: MockRole[] = [
  {
    id: 'role_admin',
    name: 'Admin',
    displayName: 'Administrator',
    description: 'System administrator with all permissions',
    isStatic: true,
    isDefault: false,
    sort: 1,
    creationTime: '2025-01-01T00:00:00Z',
  },
  {
    id: 'role_operator',
    name: 'Operator',
    displayName: 'Operator',
    description: 'Looks after logging and data retention; no access to sign-in or mail settings',
    isStatic: false,
    isDefault: false,
    sort: 50,
    creationTime: '2025-01-01T00:00:00Z',
  },
  {
    id: 'role_member',
    name: 'Member',
    displayName: 'Member',
    description: 'Default role automatically assigned to new users',
    isStatic: true,
    isDefault: true,
    sort: 100,
    creationTime: '2025-01-01T00:00:00Z',
  },
];

/**
 * 权限定义树，形状与 `GET /api/v1/permissions/definitions` 一致。Mock 只复刻端点形状，不复刻后端的
 * 决策引擎。分组标识写字面量而不引用 PERMISSIONS：分组不是权限，不参与鉴权。
 */
export const PERMISSION_DEFINITIONS = [
  {
    name: 'Work',
    displayName: 'Work',
    permissions: [
      {
        name: PERMISSIONS.dashboard.default,
        displayName: 'Dashboard',
        parentName: undefined,
        children: [],
      },
    ],
  },
  {
    name: 'Identity',
    displayName: 'Identity & access',
    permissions: [
      {
        name: PERMISSIONS.users.default,
        displayName: 'User Management',
        parentName: undefined,
        children: [
          leaf(PERMISSIONS.users.create, 'Create', PERMISSIONS.users.default),
          leaf(PERMISSIONS.users.update, 'Edit', PERMISSIONS.users.default),
          leaf(PERMISSIONS.users.delete, 'Delete', PERMISSIONS.users.default),
          leaf(PERMISSIONS.users.manageRoles, 'Assign roles', PERMISSIONS.users.default),
        ],
      },
      {
        name: PERMISSIONS.roles.default,
        displayName: 'Role Management',
        parentName: undefined,
        children: [
          leaf(PERMISSIONS.roles.create, 'Create', PERMISSIONS.roles.default),
          leaf(PERMISSIONS.roles.update, 'Edit', PERMISSIONS.roles.default),
          leaf(PERMISSIONS.roles.delete, 'Delete', PERMISSIONS.roles.default),
          leaf(
            PERMISSIONS.roles.managePermissions,
            'Configure permissions',
            PERMISSIONS.roles.default,
          ),
        ],
      },
      //#if (LocalIdentity && IncludeMultiTenancy)
      {
        name: PERMISSIONS.tenants.default,
        displayName: 'Tenant Management',
        parentName: undefined,
        children: [
          leaf(PERMISSIONS.tenants.create, 'Create', PERMISSIONS.tenants.default),
          leaf(PERMISSIONS.tenants.update, 'Edit', PERMISSIONS.tenants.default),
          leaf(PERMISSIONS.tenants.delete, 'Delete', PERMISSIONS.tenants.default),
          //#if (Impersonation)
          leaf(PERMISSIONS.tenants.impersonation, 'Sign in as tenant', PERMISSIONS.tenants.default),
          //#endif
        ],
      },
      //#endif
    ],
  },
  //#if (OpenIddictServer)
  {
    name: 'Developer',
    displayName: 'Developer',
    permissions: [
      {
        name: PERMISSIONS.openApplications.default,
        displayName: 'Open Applications',
        parentName: undefined,
        children: [
          leaf(PERMISSIONS.openApplications.create, 'Create', PERMISSIONS.openApplications.default),
          leaf(PERMISSIONS.openApplications.update, 'Edit', PERMISSIONS.openApplications.default),
          leaf(PERMISSIONS.openApplications.delete, 'Delete', PERMISSIONS.openApplications.default),
          leaf(
            PERMISSIONS.openApplications.resetSecret,
            'Reset client secret',
            PERMISSIONS.openApplications.default,
          ),
        ],
      },
    ],
  },
  //#endif
  //#if (IncludeOperationRecords)
  {
    name: 'Audit',
    displayName: 'Audit',
    permissions: [
      {
        name: PERMISSIONS.operationRecords.default,
        displayName: 'Operation records',
        parentName: undefined,
        children: [
          leaf(PERMISSIONS.operationRecords.export, 'Export', PERMISSIONS.operationRecords.default),
        ],
      },
    ],
  },
  //#endif
  {
    name: 'System',
    displayName: 'System',
    permissions: [
      {
        name: PERMISSIONS.settings.default,
        displayName: 'System settings',
        parentName: undefined,
        // 每个系统设置分组一条子权限，与后端 SettingGroupPermissions 一一对应
        children: [
          leaf(PERMISSIONS.settings.display, 'Display', PERMISSIONS.settings.default),
          //#if (LocalIdentity)
          leaf(PERMISSIONS.settings.registration, 'Registration', PERMISSIONS.settings.default),
          leaf(PERMISSIONS.settings.security, 'Sign-in & sessions', PERMISSIONS.settings.default),
          //#if (ExternalLogin)
          leaf(
            PERMISSIONS.settings.externalLogin,
            'External sign-in',
            PERMISSIONS.settings.default,
          ),
          //#endif
          //#if (Email)
          leaf(PERMISSIONS.settings.email, 'Email', PERMISSIONS.settings.default),
          //#endif
          //#endif
          leaf(PERMISSIONS.settings.operations, 'Operations', PERMISSIONS.settings.default),
          //#if (IncludeOperationRecords || IncludeNotifications)
          leaf(PERMISSIONS.settings.retention, 'Data retention', PERMISSIONS.settings.default),
          //#endif
        ],
      },
    ],
  },
];

/** 全部权限名，由定义树展开，不另手写清单。 */
export const ALL_PERMISSIONS: string[] = PERMISSION_DEFINITIONS.flatMap((group) =>
  group.permissions.flatMap((permission) => [
    permission.name,
    ...permission.children.map((child) => child.name),
  ]),
);

function leaf(name: string, displayName: string, parentName: string) {
  return { name, displayName, parentName, children: [] as never[] };
}

/**
 * 每个主体的固定授予集合与版本。
 *
 * 演示账号各自代表一种典型场景：`role_admin` 全权，`role_member` 只读用户列表，
 * 用于演示"有效权限是各来源授予的并集"；`role_operator` 只管运维与数据保留两个设置分组
 * （演示账号 zihang），用于演示系统设置按分组授权。
 */
export const PERMISSION_GRANTS: Record<string, { version: number; permissionNames: string[] }> = {
  'Role/role_admin': { version: 1, permissionNames: [...ALL_PERMISSIONS] },
  'Role/role_operator': {
    version: 1,
    // prettier-ignore
    permissionNames: [
      PERMISSIONS.settings.default,
      PERMISSIONS.settings.operations,
      //#if (IncludeOperationRecords || IncludeNotifications)
      PERMISSIONS.settings.retention,
      //#endif
    ],
  },
  'Role/role_member': { version: 1, permissionNames: [PERMISSIONS.users.default] },
};

export function grantKey(providerName: string, providerKey: string): string {
  return `${providerName}/${providerKey}`;
}
