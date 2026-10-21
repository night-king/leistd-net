import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { PagedResultDto } from '../../../shared/dtos/paged-result.dto';
import { RoleBriefDto } from '../dtos/role.dto';
import {
  BatchAssignUserRolesInputDto,
  BatchOperationOutputDto,
  BatchUsersInputDto,
  //#if (LocalIdentity)
  CreateUserInputDto,
  //#endif
  GetUsersInputDto,
  //#if (LocalIdentity)
  ResetUserPasswordInputDto,
  ResetUserTwoFactorInputDto,
  UpdateUserInputDto,
  //#endif
  UpdateUserRolesInputDto,
  UserManagementOutputDto,
} from '../dtos/user-management.dto';

@Injectable({ providedIn: 'root' })
export class UserManagementService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = '/api/v1/users';

  getUsers(input: GetUsersInputDto): Observable<PagedResultDto<UserManagementOutputDto>> {
    let params = new HttpParams();
    if (input.offset !== undefined) params = params.set('offset', input.offset.toString());
    if (input.limit !== undefined) params = params.set('limit', input.limit.toString());
    if (input.keyword) params = params.set('keyword', input.keyword);
    if (input.isActive !== undefined) params = params.set('isActive', input.isActive.toString());
    //#if (LocalIdentity)
    if (input.isEmailVerified !== undefined)
      params = params.set('isEmailVerified', input.isEmailVerified.toString());
    //#endif
    if (input.roles?.length) {
      for (const role of input.roles) {
        params = params.append('roles', role);
      }
    }
    if (input.sorting) params = params.set('sorting', input.sorting);
    return this.http.get<PagedResultDto<UserManagementOutputDto>>(this.baseUrl, { params });
  }

  getUser(id: string): Observable<UserManagementOutputDto> {
    return this.http.get<UserManagementOutputDto>(`${this.baseUrl}/${id}`);
  }

  //#if (LocalIdentity)
  createUser(data: CreateUserInputDto): Observable<UserManagementOutputDto> {
    return this.http.post<UserManagementOutputDto>(this.baseUrl, data);
  }

  updateUser(id: string, data: UpdateUserInputDto): Observable<UserManagementOutputDto> {
    return this.http.put<UserManagementOutputDto>(`${this.baseUrl}/${id}`, data);
  }

  //#endif
  enableUser(id: string): Observable<void> {
    return this.http.patch<void>(`${this.baseUrl}/${id}/enable`, {});
  }

  disableUser(id: string): Observable<void> {
    return this.http.patch<void>(`${this.baseUrl}/${id}/disable`, {});
  }

  batchEnableUsers(data: BatchUsersInputDto): Observable<BatchOperationOutputDto> {
    return this.http.post<BatchOperationOutputDto>(`${this.baseUrl}/batch/enable`, data);
  }

  batchDisableUsers(data: BatchUsersInputDto): Observable<BatchOperationOutputDto> {
    return this.http.post<BatchOperationOutputDto>(`${this.baseUrl}/batch/disable`, data);
  }
  //#if (LocalIdentity)
  resetPassword(id: string, data: ResetUserPasswordInputDto): Observable<void> {
    return this.http.post<void>(`${this.baseUrl}/${id}/reset-password`, data);
  }

  unlockUser(id: string): Observable<void> {
    return this.http.post<void>(`${this.baseUrl}/${id}/unlock`, {});
  }

  batchUnlockUsers(data: BatchUsersInputDto): Observable<BatchOperationOutputDto> {
    return this.http.post<BatchOperationOutputDto>(`${this.baseUrl}/batch/unlock`, data);
  }

  /**
   * 重置用户的两步验证（给丢了手机和恢复码的人用），该用户的会话全部失效。
   * 管理员的会话不够新时服务端要求确认身份（`Auth:ReauthenticationRequired`），带上管理员自己的
   * 当前口令或验证码重试。
   */
  resetTwoFactor(id: string, data: ResetUserTwoFactorInputDto = {}): Observable<void> {
    return this.http.post<void>(`${this.baseUrl}/${id}/reset-two-factor`, data);
  }

  deleteUser(id: string): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/${id}`);
  }
  //#endif

  getUserRoles(id: string): Observable<RoleBriefDto[]> {
    return this.http.get<RoleBriefDto[]>(`${this.baseUrl}/${id}/roles`);
  }

  /** 角色分配独立于资料更新，需要 App.Users.ManageRoles。 */
  replaceUserRoles(id: string, data: UpdateUserRolesInputDto): Observable<RoleBriefDto[]> {
    return this.http.put<RoleBriefDto[]>(`${this.baseUrl}/${id}/roles`, data);
  }

  /** 给一批用户追加角色，需要 App.Users.ManageRoles。 */
  batchAssignUserRoles(data: BatchAssignUserRolesInputDto): Observable<BatchOperationOutputDto> {
    return this.http.post<BatchOperationOutputDto>(`${this.baseUrl}/batch/assign-roles`, data);
  }
}
