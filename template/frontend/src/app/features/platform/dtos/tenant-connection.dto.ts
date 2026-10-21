/**
 * 租户的一条连接登记。每个服务可登记一条，连接名是使用方 DbContext 的连接名（default / crm）；
 * 一条都没有即不单独分库。连接串只写，任何接口都不返回，这里只有名字与版本。
 */
export interface TenantConnectionDto {
  tenantId: string;
  /** 归一化（小写）后的连接名。 */
  name: string;
  /** 并发版本：每条连接各自从 1 开始计数，每次修改递增。 */
  version: number;
}

/** 登记或更新一条连接的入参；连接名走路由，不在请求体里。 */
export interface UpsertTenantConnectionInputDto {
  /** 读到的该条连接版本；`null` 表示首次登记。必须显式给出，后端对缺字段返回 400。 */
  expectedVersion: number | null;
  /** 连接串（明文），只写。后端加密存储，之后任何接口都不会把它读回来。 */
  connectionString: string;
}

/** 连接名的合法形态，与后端 `TenantConnectionConfiguration.NamePattern` 同源。 */
export const TENANT_CONNECTION_NAME_PATTERN = /^[a-z0-9-]{1,64}$/;

/** 默认连接名：没有按名字分流的服务都解析到它。 */
export const TENANT_DEFAULT_CONNECTION_NAME = 'default';

/** 连接串长度上限，与后端入口 DTO 一致。 */
export const TENANT_CONNECTION_STRING_MAX_LENGTH = 2048;

/** 按后端规则归一化连接名（小写），先归一化再校验并提交。 */
export function normalizeTenantConnectionName(name: string): string {
  return name.trim().toLowerCase();
}
