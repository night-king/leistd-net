import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import {
  TwoFactorStatusOutputDto,
  UserSessionOutputDto,
} from '../../../shared/dtos/account-security.dto';

/**
 * 工作台读取的账户安全信息。只读：启用两步验证、退出设备等写操作都在个人设置的安全面板里完成。
 */
@Injectable({ providedIn: 'root' })
export class WorkbenchService {
  private readonly http = inject(HttpClient);

  getTwoFactorStatus(): Observable<TwoFactorStatusOutputDto> {
    return this.http.get<TwoFactorStatusOutputDto>('/api/v1/auth/me/two-factor');
  }

  /** 当前用户仍然有效的登录会话，当前设备在前。 */
  getSessions(): Observable<UserSessionOutputDto[]> {
    return this.http.get<UserSessionOutputDto[]>('/api/v1/auth/me/sessions');
  }
}
