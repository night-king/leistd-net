import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { PagedResultDto } from '../../../shared/dtos/paged-result.dto';
import {
  CreateTenantInputDto,
  GetTenantsInputDto,
  TenantOutputDto,
  UpdateTenantInputDto,
} from '../dtos/tenant.dto';

@Injectable({ providedIn: 'root' })
export class TenantService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = '/api/v1/tenants';

  getTenants(input: GetTenantsInputDto): Observable<PagedResultDto<TenantOutputDto>> {
    let params = new HttpParams();
    if (input.offset !== undefined) params = params.set('offset', input.offset.toString());
    if (input.limit !== undefined) params = params.set('limit', input.limit.toString());
    if (input.keyword) params = params.set('keyword', input.keyword);
    return this.http.get<PagedResultDto<TenantOutputDto>>(this.baseUrl, { params });
  }

  getTenant(id: string): Observable<TenantOutputDto> {
    return this.http.get<TenantOutputDto>(`${this.baseUrl}/${id}`);
  }

  createTenant(data: CreateTenantInputDto): Observable<TenantOutputDto> {
    return this.http.post<TenantOutputDto>(this.baseUrl, data);
  }

  updateTenant(id: string, data: UpdateTenantInputDto): Observable<TenantOutputDto> {
    return this.http.put<TenantOutputDto>(`${this.baseUrl}/${id}`, data);
  }

  setActivation(id: string, isActive: boolean): Observable<TenantOutputDto> {
    return this.http.put<TenantOutputDto>(`${this.baseUrl}/${id}/activation`, { isActive });
  }

  deleteTenant(id: string): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/${id}`);
  }
}
