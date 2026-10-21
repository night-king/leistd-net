import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { TenantConnectionDto, UpsertTenantConnectionInputDto } from '../dtos/tenant-connection.dto';

@Injectable({ providedIn: 'root' })
export class TenantConnectionService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = '/api/v1/tenant-connections';

  /** 列出该租户已登记的连接（不含连接串）；空数组表示它不单独分库。 */
  getConnections(tenantId: string): Observable<TenantConnectionDto[]> {
    return this.http.get<TenantConnectionDto[]>(`${this.baseUrl}/${tenantId}`);
  }

  /** 登记或更新一条连接；`name` 需已归一化。 */
  setConnection(
    tenantId: string,
    name: string,
    input: UpsertTenantConnectionInputDto,
  ): Observable<TenantConnectionDto> {
    return this.http.put<TenantConnectionDto>(
      `${this.baseUrl}/${tenantId}/${encodeURIComponent(name)}`,
      input,
    );
  }

  /** 删除一条连接：这个名字之后回落到服务自己配置的数据库。 */
  removeConnection(tenantId: string, name: string, expectedVersion: number): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/${tenantId}/${encodeURIComponent(name)}`, {
      params: new HttpParams().set('expectedVersion', expectedVersion.toString()),
    });
  }
}
