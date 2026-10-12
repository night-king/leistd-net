/**
 * 操作记录样本数据，刻意包含被拒绝的操作、模拟登录产生的行与非自然人主体
 * （`client:` / `job:` 前缀的 actorId）。
 */
export interface MockOperationRecord {
  id: string;
  action: string;
  targetId: string;
  authorizationBasis: string;
  outcome: 'succeeded' | 'failed';
  creationTime: string;
  actorId?: string;
  actorName?: string;
  impersonatorName?: string;
  correlationId?: string;
}

/** 动作定义：与后端 `OperationActionDefinitionProvider` 登记的类别、严重度同一口径。 */
export interface MockOperationActionDefinition {
  code: string;
  category: string;
  severity: 'info' | 'notice' | 'critical';
}

/** 样本用到的动作定义，取自后端登记表的同名条目；筛选项与按类别筛选都靠它，记录本身不带类别。 */
export const OPERATION_ACTION_DEFINITIONS: MockOperationActionDefinition[] = [
  { code: 'user.created', category: 'account', severity: 'info' },
  { code: 'user.updated', category: 'account', severity: 'info' },
  { code: 'user.deleted', category: 'account', severity: 'notice' },
  { code: 'user.disabled', category: 'account', severity: 'notice' },
  //#if (LocalIdentity)
  { code: 'user.password-reset', category: 'account', severity: 'notice' },
  //#endif
  { code: 'role.created', category: 'account', severity: 'info' },
  { code: 'role.deleted', category: 'account', severity: 'notice' },
  { code: 'user.roles-replaced', category: 'authorization', severity: 'critical' },
  { code: 'permission-grants.replaced', category: 'authorization', severity: 'critical' },
  { code: 'tenant.connection-registered', category: 'tenant', severity: 'critical' },
  { code: 'setting.changed', category: 'configuration', severity: 'info' },
  { code: 'operation-records.exported', category: 'data', severity: 'notice' },
];

// 固定基准时刻：样本数据不随运行时间漂移，截图与断言才稳定。
const BASE_TIME = Date.parse('2026-09-17T09:00:00.000Z');

function minutesBefore(minutes: number): string {
  return new Date(BASE_TIME - minutes * 60_000).toISOString();
}

export const OPERATION_RECORDS: MockOperationRecord[] = [
  {
    id: '0199a1f0-0001-7000-8000-000000000001',
    action: 'user.created',
    targetId: 'b6f0c4e2-1d3a-4c7b-9e11-2f5a8d0c7a01',
    authorizationBasis: 'App.Users.Create',
    outcome: 'succeeded',
    creationTime: minutesBefore(3),
    actorId: '0f6d2b91-7c44-4a2e-8f10-3b5c9d1e2a77',
    actorName: 'Alice Chen',
    correlationId: '9a1c2f7b4e6d8035',
  },
  {
    id: '0199a1f0-0002-7000-8000-000000000002',
    action: 'user.deleted',
    targetId: 'c1a7d3f8-5b2e-4d90-a6c3-7e8f1b4d2c55',
    authorizationBasis: 'App.Users.Delete',
    outcome: 'failed',
    creationTime: minutesBefore(11),
    actorId: '0f6d2b91-7c44-4a2e-8f10-3b5c9d1e2a77',
    actorName: 'Alice Chen',
    correlationId: '4b8e0d6a1f3c5729',
  },
  {
    id: '0199a1f0-0003-7000-8000-000000000003',
    action: 'role.created',
    targetId: 'a2b4c6d8-0e1f-4a3b-8c5d-6e7f809a1b2c',
    authorizationBasis: 'App.Roles.Create',
    outcome: 'succeeded',
    creationTime: minutesBefore(27),
    actorId: '5e3a8c17-9b2d-4f61-a0e8-7c4b6d9f1a33',
    actorName: 'Bob Wang',
    // 宿主管理员以租户身份操作：actorName 是被模拟的租户用户，真正按下按钮的是 impersonatorName。
    impersonatorName: 'Platform Operator',
    correlationId: '7d2f9b3e5a1c8460',
  },
  {
    id: '0199a1f0-0004-7000-8000-000000000004',
    action: 'role.deleted',
    targetId: 'f3e2d1c0-b9a8-4756-8493-2a1b0c9d8e7f',
    authorizationBasis: 'App.Roles.Delete',
    outcome: 'failed',
    creationTime: minutesBefore(45),
    actorId: '5e3a8c17-9b2d-4f61-a0e8-7c4b6d9f1a33',
    actorName: 'Bob Wang',
    correlationId: '1c6a4e8d2b7f3095',
  },
  {
    id: '0199a1f0-0005-7000-8000-000000000005',
    action: 'user.updated',
    targetId: 'b6f0c4e2-1d3a-4c7b-9e11-2f5a8d0c7a01',
    authorizationBasis: 'App.Users.Update',
    outcome: 'succeeded',
    creationTime: minutesBefore(62),
    // 机器主体：没有 GUID，只有 client:<client_id>。
    actorId: 'client:reporting-svc',
    actorName: 'Reporting service',
    correlationId: '8f0b5d2c7a3e1964',
  },
  {
    id: '0199a1f0-0006-7000-8000-000000000006',
    action: 'user.disabled',
    targetId: 'c1a7d3f8-5b2e-4d90-a6c3-7e8f1b4d2c55',
    authorizationBasis: 'App.Users.Update',
    outcome: 'succeeded',
    creationTime: minutesBefore(88),
    // 后台作业主体：前缀由宿主自定。
    actorId: 'job:nightly-cleanup',
    actorName: 'Nightly cleanup',
    correlationId: '3a9e7c1f6b0d8524',
  },
  //#if (LocalIdentity)
  // 租户分库登记只存在于本地身份形态。Resource 形态没有租户管理，这条样本连同它引用的
  // App.Tenants 权限一起随守卫裁掉——否则生成物里会留下一个该形态下根本不存在的权限名。
  {
    id: '0199a1f0-0007-7000-8000-000000000007',
    action: 'tenant.connection-registered',
    targetId: 'd4c3b2a1-9f8e-4d7c-b6a5-4938271605f4',
    authorizationBasis: 'App.Tenants.Update',
    outcome: 'failed',
    creationTime: minutesBefore(140),
    actorId: '0f6d2b91-7c44-4a2e-8f10-3b5c9d1e2a77',
    actorName: 'Alice Chen',
    correlationId: '6e1d3f9a4c7b2508',
  },
  //#endif
  {
    id: '0199a1f0-0008-7000-8000-000000000008',
    action: 'setting.changed',
    targetId: 'App.Display.TimeZone',
    authorizationBasis: 'App.Settings',
    outcome: 'succeeded',
    creationTime: minutesBefore(205),
    actorId: '5e3a8c17-9b2d-4f61-a0e8-7c4b6d9f1a33',
    actorName: 'Bob Wang',
    correlationId: '2b7f4a0e9d1c6835',
  },
  {
    id: '0199a1f0-0009-7000-8000-000000000009',
    action: 'user.created',
    targetId: 'e5d4c3b2-a190-4f8e-9d7c-6b5a49382716',
    authorizationBasis: 'App.Users.Create',
    outcome: 'succeeded',
    creationTime: minutesBefore(310),
    actorId: '0f6d2b91-7c44-4a2e-8f10-3b5c9d1e2a77',
    actorName: 'Alice Chen',
    correlationId: '5c8a2e6b0f4d9317',
  },
  {
    id: '0199a1f0-000a-7000-8000-00000000000a',
    action: 'permission-grants.replaced',
    // 与后端同形：权限授予的目标标识带提供者前缀，按目标检索时与授权阶段被拒的记录逐字一致。
    targetId: 'Role/a2b4c6d8-0e1f-4a3b-8c5d-6e7f809a1b2c',
    authorizationBasis: 'App.Roles.ManagePermissions',
    outcome: 'succeeded',
    creationTime: minutesBefore(420),
    actorId: '5e3a8c17-9b2d-4f61-a0e8-7c4b6d9f1a33',
    actorName: 'Bob Wang',
    impersonatorName: 'Platform Operator',
    correlationId: '0d3b6f1a8e5c7249',
  },
  {
    id: '0199a1f0-000b-7000-8000-00000000000b',
    action: 'user.roles-replaced',
    targetId: 'b6f0c4e2-1d3a-4c7b-9e11-2f5a8d0c7a01',
    authorizationBasis: 'App.Users.ManageRoles',
    outcome: 'failed',
    creationTime: minutesBefore(560),
    // 未认证之外的另一种"记不全"：宿主没给 name claim，只留得下标识。
    actorId: 'client:legacy-importer',
    correlationId: '7a4c9e2d0b6f1853',
  },
  //#if (LocalIdentity)
  // 重置他人密码只存在于本地身份形态，动作定义随之裁掉，样本一并守卫。
  {
    id: '0199a1f0-000c-7000-8000-00000000000c',
    action: 'user.password-reset',
    targetId: 'e5d4c3b2-a190-4f8e-9d7c-6b5a49382716',
    authorizationBasis: 'App.Users.Update',
    outcome: 'succeeded',
    creationTime: minutesBefore(700),
    actorId: '0f6d2b91-7c44-4a2e-8f10-3b5c9d1e2a77',
    actorName: 'Alice Chen',
    correlationId: '9e5b1d7f3a0c4682',
  },
  //#endif
];
