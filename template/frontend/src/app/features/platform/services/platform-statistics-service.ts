import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { PlatformStatisticsOutputDto, StatisticsDays } from '../dtos/platform-statistics.dto';

/** 平台仪表盘的统计数据，一次请求取全；需要 `App.Dashboard`。 */
@Injectable({ providedIn: 'root' })
export class PlatformStatisticsService {
  private readonly http = inject(HttpClient);

  getStatistics(days: StatisticsDays): Observable<PlatformStatisticsOutputDto> {
    return this.http.get<PlatformStatisticsOutputDto>('/api/v1/platform/statistics', {
      params: new HttpParams().set('days', days),
    });
  }
}
