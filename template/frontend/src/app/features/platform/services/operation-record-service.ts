import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { PagedResultDto } from '../../../shared/dtos/paged-result.dto';
import {
  ExportOperationRecordsInputDto,
  GetOperationRecordsInputDto,
  OperationRecordFilterOptionsDto,
  OperationRecordOutputDto,
} from '../dtos/operation-record.dto';

/** 操作记录查询服务。只读：审计记录写下后不再修改或删除。 */
@Injectable({ providedIn: 'root' })
export class OperationRecordService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = '/api/v1/operation-records';

  getOperationRecords(
    input: GetOperationRecordsInputDto,
  ): Observable<PagedResultDto<OperationRecordOutputDto>> {
    let params = new HttpParams();
    if (input.offset !== undefined) params = params.set('offset', input.offset.toString());
    if (input.limit !== undefined) params = params.set('limit', input.limit.toString());
    if (input.keyword) params = params.set('keyword', input.keyword);
    // 时间区间已是 UTC ISO 串（换算在调用方完成），这里原样透传，不再做第二次转换。
    if (input.startTime) params = params.set('startTime', input.startTime);
    if (input.endTime) params = params.set('endTime', input.endTime);
    // 数组参数逐个 append（服务端是 List<string>，查询串为重复键），set 会只保留最后一个值。
    if (input.categories?.length) {
      for (const category of input.categories) {
        params = params.append('categories', category);
      }
    }
    if (input.actions?.length) {
      for (const action of input.actions) {
        params = params.append('actions', action);
      }
    }
    if (input.outcome) params = params.set('outcome', input.outcome);
    return this.http.get<PagedResultDto<OperationRecordOutputDto>>(this.baseUrl, { params });
  }

  /** 取筛选项（类别与动作码），已按当前读者的可见性裁剪；前端不硬编码动作码。 */
  getFilterOptions(): Observable<OperationRecordFilterOptionsDto> {
    return this.http.get<OperationRecordFilterOptionsDto>(`${this.baseUrl}/filter-options`);
  }

  /**
   * 按当前筛选条件导出 CSV（前 N 条，后端 10000 封顶）。`responseType: 'blob'` 不能省，
   * 否则按 JSON 解析会失败。
   */
  exportOperationRecords(input: ExportOperationRecordsInputDto): Observable<Blob> {
    let params = new HttpParams();
    if (input.keyword) params = params.set('keyword', input.keyword);
    if (input.startTime) params = params.set('startTime', input.startTime);
    if (input.endTime) params = params.set('endTime', input.endTime);
    // 数组参数逐个 append，与查询方法同一理由：set 会覆盖成只传最后一个值且不报错。
    if (input.categories?.length) {
      for (const category of input.categories) {
        params = params.append('categories', category);
      }
    }
    if (input.actions?.length) {
      for (const action of input.actions) {
        params = params.append('actions', action);
      }
    }
    if (input.outcome) params = params.set('outcome', input.outcome);
    if (input.limit !== undefined) params = params.set('limit', input.limit.toString());
    return this.http.get(`${this.baseUrl}/export`, { params, responseType: 'blob' });
  }
}
