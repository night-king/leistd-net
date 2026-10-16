import { RoleBriefDto } from './role.dto';
import { PagedRequestDto } from '../../../shared/dtos/paged-request.dto';

export interface UserManagementOutputDto {
  id: string;
  username: string;
  email: string;
  displayName?: string;
  avatar?: string;
  isActive: boolean;
  //#if (LocalIdentity)
  isEmailVerified: boolean;
  //#endif
  /** 已分配角色。Id 用于提交，name/displayName 只用于展示。 */
  roles?: RoleBriefDto[];
  isSuperAdmin: boolean;
  creationTime: string;
  //#if (LocalIdentity)
  lastLoginTime?: string;
  /** 登录锁定正在生效（登录失败触发的临时锁定，或管理员锁定）。 */
  isLockedOut?: boolean;
  /** 锁定截止时间；无期限时为空。 */
  lockoutEnd?: string | null;
  isTwoFactorEnabled?: boolean;
  //#endif
}

export interface GetUsersInputDto extends PagedRequestDto {
  keyword?: string;
  isActive?: boolean;
  //#if (LocalIdentity)
  isEmailVerified?: boolean;
  //#endif
  roles?: string[];
}

//#if (LocalIdentity)
export interface CreateUserInputDto {
  username: string;
  email: string;
  displayName?: string;
  avatar?: string;
  password: string;
  isActive: boolean;
  isEmailVerified: boolean;
  /**
   * 初始角色 Id 集合。非空时后端额外要求 App.Users.ManageRoles，
   * 只持有创建权限的主体无法在创建时造出管理员。
   */
  roleIds: string[];
}
//#endif
/** 替换用户角色。与资料更新是两个独立命令、两个独立权限。 */
export interface UpdateUserRolesInputDto {
  roleIds: string[];
}

/** 不含角色字段：角色分配走 PUT /api/v1/users/{id}/roles 并要求 App.Users.ManageRoles。 */
/** 不含启用状态：改变账号可用性只有启用/禁用两个命令一个入口，保护规则也只写在那里。 */
//#if (LocalIdentity)
export interface UpdateUserInputDto {
  email: string;
  displayName?: string;
  avatar?: string;
  isEmailVerified: boolean;
  /** 管理员本人的当前口令：改邮箱或标为已验证而会话不够新时带上，与 `twoFactorCode` 二选一。 */
  password?: string;
  /** 管理员本人身份验证器上的 6 位验证码，与 `password` 二选一。 */
  twoFactorCode?: string;
}
export interface ResetUserPasswordInputDto {
  /** 给对方设的新口令。 */
  password: string;
  /** 管理员本人的当前口令：会话不够新时带上，不是对方的口令；与 `twoFactorCode` 二选一。 */
  currentPassword?: string;
  /** 管理员本人身份验证器上的 6 位验证码，与 `currentPassword` 二选一。 */
  twoFactorCode?: string;
}
/** 重置他人的两步验证：会话不够新时带上管理员本人的当前口令或验证码（二选一）。 */
export interface ResetUserTwoFactorInputDto {
  password?: string;
  twoFactorCode?: string;
}
//#endif

/** 批量操作的用户：1–100 个 Id，重复的只处理一次。 */
export interface BatchUsersInputDto {
  ids: string[];
}

/** 批量追加角色：只追加不覆盖，已有的角色保持不变；角色 1–20 个，有不存在的整批拒绝。 */
export interface BatchAssignUserRolesInputDto extends BatchUsersInputDto {
  roleIds: string[];
}

/** 一个用户的执行结果。失败时带单条接口同样的错误码与本地化说明；成功时两者省略。 */
export interface BatchOperationItemOutputDto {
  id: string;
  succeeded: boolean;
  errorCode?: string | null;
  message?: string | null;
}

/** 批量操作逐条执行，部分失败不回滚已成功的条目。 */
export interface BatchOperationOutputDto {
  items: BatchOperationItemOutputDto[];
}
