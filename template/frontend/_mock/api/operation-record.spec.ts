import {
  escapeCsv,
  exportOperationRecords,
  getOperationRecordFilterOptions,
  getOperationRecords,
  MAXIMUM_EXPORT_COUNT,
  OPERATION_RECORD_API,
} from './operation-record';
import { MockException } from '../core/models';
import {
  MockOperationRecord,
  OPERATION_ACTION_DEFINITIONS,
  OPERATION_RECORDS,
} from '../data/operation-record';

/**
 * Mock 与真实后端的契约对齐：筛选按类别展开成动作码、筛选项随动作定义下发、
 * 导出返回带 BOM 的 CSV Blob 并留下一条导出记录。
 */
describe('operation record mock', () => {
  let snapshot: MockOperationRecord[];

  beforeEach(() => {
    snapshot = OPERATION_RECORDS.map((record) => ({ ...record }));
  });

  afterEach(() => {
    OPERATION_RECORDS.length = 0;
    OPERATION_RECORDS.push(...snapshot);
  });

  function categoryOf(action: string): string | undefined {
    return OPERATION_ACTION_DEFINITIONS.find((definition) => definition.code === action)?.category;
  }

  function expectValidationError(action: () => unknown, field: string): void {
    let thrown: unknown;
    try {
      action();
    } catch (error) {
      thrown = error;
    }
    expect(thrown).toBeInstanceOf(MockException);
    const exception = thrown as MockException;
    expect(exception.status).toBe(400);
    expect(exception.error.errors.map((error: { field: string }) => error.field)).toContain(field);
  }

  async function readCsv(params: Record<string, unknown>): Promise<string[]> {
    const response = exportOperationRecords(params);
    expect(response.status).toBe(200);
    expect(response.headers?.get('Content-Type')).toBe('text/csv');
    const blob = response.body as Blob;
    expect(blob).toBeInstanceOf(Blob);
    const bytes = new Uint8Array(await blob.arrayBuffer());
    // UTF-8 BOM：EF BB BF
    expect([...bytes.slice(0, 3)]).toEqual([0xef, 0xbb, 0xbf]);
    return new TextDecoder().decode(bytes.slice(3)).trimEnd().split('\r\n');
  }

  it('registers list, filter options and export routes', () => {
    expect(Object.keys(OPERATION_RECORD_API)).toEqual([
      'GET /api/v1/operation-records',
      'GET /api/v1/operation-records/filter-options',
      'GET /api/v1/operation-records/export',
    ]);
  });

  it('gives every sample record an action definition', () => {
    for (const record of OPERATION_RECORDS) {
      expect(categoryOf(record.action), record.action).toBeDefined();
    }
  });

  it('returns distinct categories and every action with category and severity', () => {
    const options = getOperationRecordFilterOptions();

    expect(options.categories).toEqual([...new Set(options.categories)]);
    expect(options.categories).toContain('account');
    expect(options.actions).toHaveLength(OPERATION_ACTION_DEFINITIONS.length);
    for (const action of options.actions) {
      expect(options.categories).toContain(action.category);
      expect(['info', 'notice', 'critical']).toContain(action.severity);
    }
  });

  it('expands categories into actions when filtering the list', () => {
    const page = getOperationRecords({ categories: ['authorization'], limit: '50' });

    expect(page.totalCount).toBeGreaterThan(0);
    expect(page.items.every((record) => categoryOf(record.action) === 'authorization')).toBe(true);
  });

  it('intersects categories with explicit actions', () => {
    const page = getOperationRecords({
      categories: ['account'],
      actions: ['user.created', 'permission-grants.replaced'],
      limit: '50',
    });

    expect(page.totalCount).toBeGreaterThan(0);
    expect(page.items.every((record) => record.action === 'user.created')).toBe(true);
  });

  it('returns an empty page when the filter expands to no action', () => {
    const page = getOperationRecords({ categories: ['no-such-category'] });

    expect(page).toEqual({ totalCount: 0, items: [] });
  });

  it('filters by outcome', () => {
    const page = getOperationRecords({ outcome: 'failed', limit: '50' });

    expect(page.totalCount).toBeGreaterThan(0);
    expect(page.items.every((record) => record.outcome === 'failed')).toBe(true);
  });

  it('rejects an unknown outcome and a reversed time range with field errors', () => {
    expectValidationError(() => getOperationRecords({ outcome: 'Unknown' }), 'outcome');
    expectValidationError(() => getOperationRecords({ outcome: 'Failed' }), 'outcome');
    expectValidationError(() => getOperationRecords({ outcome: '1' }), 'outcome');
    expectValidationError(
      () =>
        getOperationRecords({
          startTime: '2026-09-17T09:00:00.000Z',
          endTime: '2026-09-17T08:00:00.000Z',
        }),
      'startTime',
    );
  });

  it('exports the filtered records as csv with host columns', async () => {
    const expected = getOperationRecords({ outcome: 'failed', limit: '50' }).totalCount;

    const lines = await readCsv({ outcome: 'failed' });

    expect(lines[0].split(',')).toEqual(expect.arrayContaining(['Action', 'CorrelationId']));
    expect(lines).toHaveLength(expected + 1);
    expect(lines.slice(1).every((line) => line.split(',')[2] === 'failed')).toBe(true);
  });

  it('caps the export at the requested limit', async () => {
    const lines = await readCsv({ limit: '2' });

    expect(lines).toHaveLength(3);
  });

  it('exports only the header when the filter expands to no action', async () => {
    const lines = await readCsv({ categories: ['no-such-category'] });

    expect(lines).toHaveLength(1);
  });

  it('records the export after building the file', async () => {
    const before = OPERATION_RECORDS.length;

    const lines = await readCsv({});

    expect(lines).toHaveLength(before + 1);
    expect(OPERATION_RECORDS).toHaveLength(before + 1);
    expect(OPERATION_RECORDS.at(-1)).toEqual(
      expect.objectContaining({ action: 'operation-records.exported', outcome: 'succeeded' }),
    );
    expect(getOperationRecords({}).items[0].action).toBe('operation-records.exported');
  });

  it('rejects an export limit outside the allowed range without recording', () => {
    const before = OPERATION_RECORDS.length;

    expectValidationError(() => exportOperationRecords({ limit: '0' }), 'limit');
    expectValidationError(
      () => exportOperationRecords({ limit: String(MAXIMUM_EXPORT_COUNT + 1) }),
      'limit',
    );
    expect(OPERATION_RECORDS).toHaveLength(before);
  });

  it('neutralizes formula prefixes and quotes separators', () => {
    expect(escapeCsv('=SUM(A1)')).toBe("'=SUM(A1)");
    expect(escapeCsv('a,b')).toBe('"a,b"');
    expect(escapeCsv('say "hi"')).toBe('"say ""hi"""');
    expect(escapeCsv('plain')).toBe('plain');
  });
});
