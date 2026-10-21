/** 当前用户的有效权限。 */
export interface CurrentPermissionsOutputDto {
  permissions: string[];
  isSuperAdmin: boolean;
  /** 有效权限的版本标记，变化即表示本地缓存已过期。 */
  versionToken: string;
}

export interface PermissionDefinitionOutputDto {
  name: string;
  displayName: string;
  parentName?: string;
  children: PermissionDefinitionOutputDto[];
}

export interface PermissionDefinitionGroupOutputDto {
  name: string;
  displayName: string;
  permissions: PermissionDefinitionOutputDto[];
}

/** 单个权限在某角色上的授予状态。授予是纯加法，只有已授予与未授予两种。 */
export interface PermissionGrantStateDto {
  name: string;
  granted: boolean;
}

export interface PermissionGrantsOutputDto {
  providerName: string;
  providerKey: string;
  /** 乐观并发版本，保存时原样回传。 */
  version: number;
  grants: PermissionGrantStateDto[];
}

export interface ReplacePermissionGrantsInputDto {
  expectedVersion: number;
  /** 目标权限名集合，未出现的权限视为撤销。 */
  permissionNames: string[];
}
