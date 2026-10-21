import { PagedRequestDto } from '../../../shared/dtos/paged-request.dto';

export interface RoleOutputDto {
  id: string;
  /** 角色名称，唯一且创建后不可修改，作为稳定的业务标识。 */
  name: string;
  displayName: string;
  description?: string;
  /** 系统内置角色，不可删除。 */
  isStatic: boolean;
  /** 默认角色，新用户自动分配。 */
  isDefault: boolean;
  sort: number;
  userCount: number;
  permissionCount: number;
  creationTime: string;
  lastModificationTime?: string;
}

/** 角色简要信息，用于用户角色分配等选择场景。 */
export interface RoleBriefDto {
  id: string;
  name: string;
  displayName: string;
}

export interface GetRolesInputDto extends PagedRequestDto {
  keyword?: string;
}

export interface CreateRoleInputDto {
  name: string;
  displayName: string;
  description?: string;
  sort: number;
  isDefault: boolean;
}

export interface UpdateRoleInputDto {
  displayName: string;
  description?: string;
  sort: number;
  isDefault: boolean;
}
