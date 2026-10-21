import { TenantConnectionDto } from '../../src/app/features/platform/dtos/tenant-connection.dto';
import { TenantOutputDto } from '../../src/app/features/platform/dtos/tenant.dto';

/** 一条连接登记；Mock 不保存连接串（真实后端加密存储、从不返回）。 */
export interface MockTenantConnection {
  name: string;
  version: number;
}

export interface MockTenant {
  id: string;
  name: string;
  displayName?: string;
  description?: string;
  isActive: boolean;
  creationTime: string;
  /** 该租户已登记的连接，按名字唯一；空数组即不单独分库。 */
  connections: MockTenantConnection[];
}

// 一启用一停用：覆盖登录页「租户已停用」与登录 403 的演示路径。
// 连接也一空一多：空数组是"不分库"那一档，globex 的两条覆盖"同一租户在多个服务各登记一条"。
export const TENANTS: MockTenant[] = [
  {
    id: 'tenant_acme',
    name: 'acme',
    displayName: 'Acme Corp',
    description: '示例租户：不单独分库',
    isActive: true,
    creationTime: '2025-03-01T00:00:00Z',
    connections: [],
  },
  {
    id: 'tenant_globex',
    name: 'globex',
    displayName: 'Globex Inc',
    isActive: false,
    creationTime: '2025-04-15T00:00:00Z',
    connections: [
      { name: 'default', version: 3 },
      { name: 'crm', version: 1 },
    ],
  },
];

export function toTenantOutput(tenant: MockTenant): TenantOutputDto {
  return {
    id: tenant.id,
    name: tenant.name,
    displayName: tenant.displayName,
    description: tenant.description,
    isActive: tenant.isActive,
    creationTime: tenant.creationTime,
  };
}

/** 连接投影：只给名字与版本，任何情况下都不返回连接串。 */
export function toTenantConnection(
  tenant: MockTenant,
  connection: MockTenantConnection,
): TenantConnectionDto {
  return {
    tenantId: tenant.id,
    name: connection.name,
    version: connection.version,
  };
}
