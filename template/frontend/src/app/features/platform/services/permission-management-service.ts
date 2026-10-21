import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import {
  PermissionDefinitionGroupOutputDto,
  PermissionGrantsOutputDto,
  ReplacePermissionGrantsInputDto,
} from '../../../shared/dtos/permission.dto';

/** 权限定义与授予的管理端接口：权限树由后端生成，保存以主体为单位一次性替换并携带版本号。 */
@Injectable({ providedIn: 'root' })
export class PermissionManagementService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = '/api/v1/permissions';

  getDefinitions(): Observable<PermissionDefinitionGroupOutputDto[]> {
    return this.http.get<PermissionDefinitionGroupOutputDto[]>(`${this.baseUrl}/definitions`);
  }

  getRoleGrants(roleId: string): Observable<PermissionGrantsOutputDto> {
    return this.http.get<PermissionGrantsOutputDto>(`${this.baseUrl}/grants/roles/${roleId}`);
  }

  replaceRoleGrants(
    roleId: string,
    data: ReplacePermissionGrantsInputDto,
  ): Observable<PermissionGrantsOutputDto> {
    return this.http.put<PermissionGrantsOutputDto>(`${this.baseUrl}/grants/roles/${roleId}`, data);
  }
}
