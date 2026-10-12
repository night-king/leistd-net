import { HttpHeaders } from '@angular/common/http';

import { PagedResultDto } from '../../src/app/shared/dtos/paged-result.dto';
import { MockException, MockRequest, MockResponse } from '../core/models';
import {
  MockOperationRecord,
  OPERATION_ACTION_DEFINITIONS,
  OPERATION_RECORDS,
} from '../data/operation-record';
import { getCurrentUser } from '../utils/current-user';

/** 与后端 `ExportOperationRecordsInputDto.MaximumExportCount` 同值。 */
export const MAXIMUM_EXPORT_COUNT = 10000;

/** 与后端查询串同一口径：只认 camelCase 名称，数字与其他大小写都按字段 `outcome` 拒绝。 */
const OUTCOMES = ['succeeded', 'failed'];

// 导出记录的标识按序号生成：crypto.randomUUID 只在安全上下文可用，经局域网地址打开开发服务器时不存在。
let exportSequence = 0;

function getQueryValue(value: unknown) {
  const normalized = Array.isArray(value) ? value[0] : value;
  return normalized === undefined || normalized === null || normalized === ''
    ? undefined
    : String(normalized);
}

/** 重复键的查询参数（`?categories=a&categories=b`）收成数组；空串视为没传。 */
function getQueryValues(value: unknown): string[] {
  if (value === undefined || value === null) {
    return [];
  }
  return (Array.isArray(value) ? value : [value]).map(String).filter((item) => item !== '');
}

/**
 * 与后端 `ResolveRequestedActions` 同一口径：类别先展开成动作码，再与显式给的动作码取交集。
 * 返回 `null` 表示没有按动作筛选；返回空数组表示"筛了但展开为空"，调用方必须返回空结果而不是全量。
 */
function resolveRequestedActions(categories: string[], actions: string[]): string[] | null {
  if (categories.length === 0 && actions.length === 0) {
    return null;
  }
  let codes = OPERATION_ACTION_DEFINITIONS.map((definition) => definition.code);
  if (categories.length > 0) {
    codes = OPERATION_ACTION_DEFINITIONS.filter((definition) =>
      categories.includes(definition.category),
    ).map((definition) => definition.code);
  }
  return actions.length > 0 ? codes.filter((code) => actions.includes(code)) : codes;
}

/** 与后端入参校验同形：400 字段错误、不带业务码，字段名跟随 JSON 命名。 */
function validateFilters(params: any, limit?: number): void {
  const errors: { field: string; detail: string }[] = [];
  const outcome = getQueryValue(params.outcome);
  if (outcome !== undefined && !OUTCOMES.includes(outcome)) {
    errors.push({ field: 'outcome', detail: 'outcome is not an allowed value.' });
  }
  const startTime = getQueryValue(params.startTime);
  const endTime = getQueryValue(params.endTime);
  if (startTime && endTime && startTime > endTime) {
    const detail = 'The start time must not be later than the end time.';
    errors.push({ field: 'startTime', detail }, { field: 'endTime', detail });
  }
  if (limit !== undefined && !(limit >= 1 && limit <= MAXIMUM_EXPORT_COUNT)) {
    errors.push({ field: 'limit', detail: `limit must be between 1 and ${MAXIMUM_EXPORT_COUNT}.` });
  }
  if (errors.length > 0) {
    throw new MockException(400, { errors });
  }
}

/**
 * 与后端 `EfCoreOperationRecordStore.GetPagedListAsync` 同一口径：
 * 固定按时间倒序，并追加 Id 作为稳定次序——否则同一毫秒内的多条记录在翻页时会重复或漏掉。
 */
function filterRecords(params: any): MockOperationRecord[] {
  const requestedActions = resolveRequestedActions(
    getQueryValues(params.categories),
    getQueryValues(params.actions),
  );
  if (requestedActions?.length === 0) {
    return [];
  }

  let records = [...OPERATION_RECORDS];
  const keyword = getQueryValue(params.keyword)?.toLowerCase();
  if (keyword) {
    records = records.filter(
      (record) =>
        record.action.toLowerCase().includes(keyword) ||
        record.targetId.toLowerCase().includes(keyword) ||
        record.actorName?.toLowerCase().includes(keyword) ||
        record.actorId?.toLowerCase().includes(keyword),
    );
  }

  // 与后端同为闭区间；两端与 `creationTime` 都是 UTC ISO 串，字典序即时间序。
  const startTime = getQueryValue(params.startTime);
  const endTime = getQueryValue(params.endTime);
  if (startTime) {
    records = records.filter((record) => record.creationTime >= startTime);
  }
  if (endTime) {
    records = records.filter((record) => record.creationTime <= endTime);
  }

  if (requestedActions) {
    records = records.filter((record) => requestedActions.includes(record.action));
  }
  const outcome = getQueryValue(params.outcome);
  if (outcome) {
    records = records.filter((record) => record.outcome === outcome);
  }

  return records.sort((a, b) => {
    const compared = b.creationTime.localeCompare(a.creationTime);
    return compared !== 0 ? compared : b.id.localeCompare(a.id);
  });
}

/** 后端契约没有排序参数。 */
export function getOperationRecords(params: any): PagedResultDto<any> {
  validateFilters(params);
  const offset = +(getQueryValue(params.offset) ?? 0);
  const limit = +(getQueryValue(params.limit) ?? 10);
  const records = filterRecords(params);

  return {
    totalCount: records.length,
    items: records.slice(offset, offset + limit),
  };
}

/** 筛选项：出现过的类别（去重、保持登记顺序）与全部动作定义。 */
export function getOperationRecordFilterOptions() {
  return {
    categories: [...new Set(OPERATION_ACTION_DEFINITIONS.map((definition) => definition.category))],
    actions: OPERATION_ACTION_DEFINITIONS.map((definition) => ({ ...definition })),
  };
}

const CSV_HEADER = [
  'CreationTime(UTC)',
  'Action',
  'Outcome',
  'ActorId',
  'ActorName',
  'ImpersonatorName',
  'TargetId',
  'TargetName',
  'AuthorizationBasis',
  'FailureCode',
  'FailureData',
  'FailureMessage',
  'FailureDetail',
  'CorrelationId',
  'ActorTenantId',
];

/** 与后端同一转义：以 `= + - @` 等开头的字段前缀单引号防公式注入，含逗号、引号或换行时整格加引号。 */
export function escapeCsv(value: string): string {
  const neutralized = /^[=+\-@\t\r]/.test(value) ? `'${value}` : value;
  return /[",\n\r]/.test(neutralized) ? `"${neutralized.replace(/"/g, '""')}"` : neutralized;
}

function toCsv(records: MockOperationRecord[]): string {
  const rows = records.map((record) =>
    [
      record.creationTime,
      record.action,
      record.outcome,
      record.actorId,
      record.actorName,
      record.impersonatorName,
      record.targetId,
      undefined,
      record.authorizationBasis,
      undefined,
      undefined,
      undefined,
      undefined,
      record.correlationId,
      undefined,
    ].map((cell) => escapeCsv(cell ?? '')),
  );
  const lines = [CSV_HEADER, ...rows].map((cells) => cells.join(','));
  return `${lines.join('\r\n')}\r\n`;
}

/**
 * 按筛选条件导出 CSV（Mock 读者是宿主，带仅宿主可见的列），文件生成之后记一条导出记录，与后端顺序一致。
 *
 * 返回 `Blob`：前端以 `responseType: 'blob'` 读取，拦截器不会替 Mock 把字符串转成 Blob。
 */
export function exportOperationRecords(params: any): MockResponse {
  const limit = +(getQueryValue(params.limit) ?? MAXIMUM_EXPORT_COUNT);
  validateFilters(params, limit);
  const content = toCsv(filterRecords(params).slice(0, limit));

  const actor = getCurrentUser();
  OPERATION_RECORDS.push({
    id: `0199a1f0-ffff-7000-8000-${String(++exportSequence).padStart(12, '0')}`,
    action: 'operation-records.exported',
    targetId: '-',
    authorizationBasis: 'App.OperationRecords.Export',
    outcome: 'succeeded',
    creationTime: new Date().toISOString(),
    actorId: actor?.id,
    actorName: actor?.displayName ?? actor?.username,
  });

  return {
    status: 200,
    headers: new HttpHeaders({ 'Content-Type': 'text/csv' }),
    // 与后端一样带 UTF-8 BOM：否则 Excel 按本地代码页解释，中文全是乱码。
    body: new Blob(['﻿', content], { type: 'text/csv' }),
  };
}

// 只读：记录写下之后不再修改或删除，因此没有写端点。
export const OPERATION_RECORD_API = {
  'GET /api/v1/operation-records': (req: MockRequest) => getOperationRecords(req.queryParams),
  'GET /api/v1/operation-records/filter-options': () => getOperationRecordFilterOptions(),
  'GET /api/v1/operation-records/export': (req: MockRequest) =>
    exportOperationRecords(req.queryParams),
};
