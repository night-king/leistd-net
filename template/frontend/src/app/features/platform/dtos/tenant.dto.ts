import { PagedRequestDto } from '../../../shared/dtos/paged-request.dto';

/**
 * 租户名的合法形态：单个 DNS 标签（按子域名解析时即主机名的一段），与后端
 * `TenantConfiguration.NamePattern` 同源。
 */
export const TENANT_NAME_PATTERN = /^[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?$/;

/** 租户名长度上限（DNS 标签上限），与后端 `TenantConfiguration.MaxNameLength` 一致。 */
export const TENANT_NAME_MAX_LENGTH = 63;

export interface TenantOutputDto {
  id: string;
  /** 租户名称，唯一，作为稳定的业务标识（登录时按名称解析租户）。 */
  name: string;
  displayName?: string;
  /** 简短描述，说明该租户的用途。 */
  description?: string;
  /** 停用的租户其用户无法登录。 */
  isActive: boolean;
  creationTime: string;
}

export interface GetTenantsInputDto extends PagedRequestDto {
  keyword?: string;
}

export interface CreateTenantInputDto {
  name: string;
  displayName?: string;
  description?: string;
  /** 租户初始管理员账号。 */
  adminEmail: string;
  adminPassword: string;
  /**
   * 该租户的专属库连接，按名字登记；留空数组即不分库。分库只能在建租户时定案：登记先于播种，
   * 建好后再分库会被 409 拒绝。库须事先建好并迁移；租户与全部连接在同一事务里落库。
   */
  connections?: CreateTenantConnectionInputDto[];
}

/** 创建租户时登记的一条命名连接。 */
export interface CreateTenantConnectionInputDto {
  /** 连接名，对应使用方服务的连接名；不区分大小写，后端归一化为小写。 */
  name: string;
  connectionString: string;
}

export interface UpdateTenantInputDto {
  name: string;
  displayName?: string;
  /** 描述；整体覆盖，省略与 `null` 都会清空，要保留须把原值一起传回。 */
  description?: string | null;
}

export interface SetTenantActivationInputDto {
  isActive: boolean;
}
