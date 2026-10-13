import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
//#if (IncludeLocalization)
import { TranslocoDirective } from '@jsverse/transloco';
//#endif

import { operationFailureReason, operationRecordActor } from './operation-record-texts';
import { SettingContextService } from '../../../../../../core/settings/setting-context-service';
import { AppDate } from '../../../../../../shared/pipes/app-date-pipe';
//#if (!IncludeLocalization)
import { englishText } from '../../../../../../shared/utils/english-text';
//#endif
import { OperationRecordOutputDto } from '../../../../dtos/operation-record.dto';

/**
 * 一条操作记录的追查字段：目标标识、失败原因与技术详情、授权依据、操作人租户、链路标识。操作记录表的行展开
 * 与仪表盘的最近操作共用；列表里已经显示的操作人与时间由调用方决定是否再列。
 */
@Component({
  selector: 'app-operation-record-detail',
  // 裁掉本地化后只剩一项，prettier 会要求折成一行，与另一生成物冲突；固定书写形态
  // prettier-ignore
  imports: [
    AppDate,
    //#if (IncludeLocalization)
    TranslocoDirective,
    //#endif
  ],
  templateUrl: './operation-record-detail.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class OperationRecordDetail {
  protected readonly displayTimeZone = inject(SettingContextService).timeZone;
  protected readonly displayLocale = inject(SettingContextService).displayLocale;
  //#if (!IncludeLocalization)
  protected readonly t = englishText(ENGLISH);
  //#endif

  readonly record = input.required<OperationRecordOutputDto>();
  /** 列表里操作人一列被裁掉时补上。 */
  readonly showActor = input(false);
  /** 列表里没有完整时间时补上（裁掉了时间列，或只显示了相对时间）。 */
  readonly showTime = input(false);

  protected readonly actor = computed(() => operationRecordActor(this.record()));
  protected readonly failure = computed(() => operationFailureReason(this.record()));
}
//#if (!IncludeLocalization)

/** 不含本地化时的界面文案，与 `en.json` 同步。 */
const ENGLISH: Record<string, string> = {
  'operationRecords.table.colActor': 'Operator',
  'operationRecords.table.unknownActor': 'Anonymous',
  'operationRecords.table.impersonatedBy': 'acting on behalf, by {{name}}',
  'operationRecords.table.colTarget': 'Target',
  'operationRecords.table.colFailure': 'Reason',
  'operationRecords.table.colFailureDetail': 'Technical detail',
  'operationRecords.table.colTime': 'Time',
  'operationRecords.table.colBasis': 'Authorized by',
  'operationRecords.table.colActorTenantId': 'Operator tenant',
  'operationRecords.table.colCorrelationId': 'Trace ID',
};
//#endif
