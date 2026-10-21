import { HttpHeaders, HttpRequest } from '@angular/common/http';

import { OPEN_APPLICATION_API } from './open-application';
import { CreateOpenApplicationInputDto } from '../../src/app/features/platform/dtos/open-application.dto';
import { MockException } from '../core/models';
import { OPEN_APPLICATIONS } from '../data/open-applications';
import { setMockSessionUserId } from '../utils/current-user';

type Handler = (req: { body?: unknown; params?: Record<string, string> }) => unknown;

function rejectionOf(run: () => unknown): MockException {
  try {
    run();
  } catch (error) {
    expect(error).toBeInstanceOf(MockException);
    return error as MockException;
  }
  throw new Error('expected the handler to reject');
}

/**
 * 开放应用 Mock 的失败形态与后端一致：入参校验是 400 字段错误（不带业务码，一次报全），
 * 先于唯一性等业务规则；删除幂等。
 */
describe('open application mock', () => {
  const create = OPEN_APPLICATION_API['POST /api/v1/open-applications'] as Handler;
  const update = OPEN_APPLICATION_API['PUT /api/v1/open-applications/:id'] as Handler;
  const remove = OPEN_APPLICATION_API['DELETE /api/v1/open-applications/:id'] as Handler;
  let snapshot: typeof OPEN_APPLICATIONS;

  const valid: CreateOpenApplicationInputDto = {
    clientId: 'mock-spec-client',
    displayName: 'Spec client',
    applicationType: 'web',
    clientType: 'confidential',
    redirectUris: ['https://app.example.test/callback'],
    postLogoutRedirectUris: [],
    permissions: [],
    requirements: [],
    sessionBound: false,
  };

  beforeEach(() => {
    snapshot = [...OPEN_APPLICATIONS];
    setMockSessionUserId('user_admin');
  });

  afterEach(() => {
    OPEN_APPLICATIONS.length = 0;
    OPEN_APPLICATIONS.push(...snapshot);
    setMockSessionUserId(null);
  });

  function fieldsOf(error: MockException): string[] {
    return error.error.errors.map((item: { field: string }) => item.field);
  }

  it('supports multiple sorting keys and rejects private metadata with an empty result', () => {
    const list = OPEN_APPLICATION_API['GET /api/v1/open-applications'];
    const request = {
      original: new HttpRequest('GET', '/api/v1/open-applications'),
      url: '/api/v1/open-applications',
      headers: new HttpHeaders(),
      body: null,
      params: {},
    };
    OPEN_APPLICATIONS.splice(
      0,
      OPEN_APPLICATIONS.length,
      ...OPEN_APPLICATIONS.map((item) => ({ ...item, displayName: 'Same' })),
    );
    const result = list({
      ...request,
      queryParams: { sorting: 'DISPLAYNAME asc, clientId desc', limit: 100 },
    });
    const expected = snapshot
      .map((item) => item.clientId)
      .sort()
      .reverse();
    expect(result.items.map((item) => item.clientId)).toEqual(expected);

    const error = rejectionOf(() =>
      list({
        ...request,
        queryParams: { keyword: 'missing', sorting: 'Application.ClientSecret' },
      }),
    );
    expect(error.status).toBe(400);
    expect(error.error.code).toBeUndefined();
    expect(fieldsOf(error)).toEqual(['sorting']);
  });

  it('reports every missing field at once as 400 field errors without a code', () => {
    const error = rejectionOf(() =>
      create({ body: { ...valid, clientId: '  ', sessionBound: null } }),
    );

    expect(error.status).toBe(400);
    expect(error.error.code).toBeUndefined();
    expect(fieldsOf(error)).toEqual(['clientId', 'sessionBound']);
    expect(OPEN_APPLICATIONS).toEqual(snapshot);
  });

  it('rejects application and client types outside the allowed values as field errors', () => {
    const body = { ...valid, applicationType: 'spa', clientType: 'secret' };

    const created = rejectionOf(() => create({ body }));
    expect(created.status).toBe(400);
    expect(created.error.code).toBeUndefined();
    expect(fieldsOf(created)).toEqual(['applicationType', 'clientType']);
    expect(OPEN_APPLICATIONS).toEqual(snapshot);

    const target = snapshot[0];
    const before = { ...target };
    const updated = rejectionOf(() => update({ params: { id: target.id }, body }));
    expect(updated.status).toBe(400);
    expect(updated.error.code).toBeUndefined();
    expect(fieldsOf(updated)).toEqual(['applicationType', 'clientType']);
    expect(target).toEqual(before);
  });

  it.each([
    ['a relative URI', 'callback'],
    ['whitespace', 'https://app.example.test/call back'],
    ['a fragment', 'https://app.example.test/callback#done'],
  ])('rejects a callback URI with %s as a field error', (_, uri) => {
    const error = rejectionOf(() =>
      create({ body: { ...valid, redirectUris: [uri], postLogoutRedirectUris: [uri] } }),
    );

    expect(error.status).toBe(400);
    expect(fieldsOf(error)).toEqual(['redirectUris', 'postLogoutRedirectUris']);
  });

  it('checks the field errors before the client ID uniqueness', () => {
    const taken = snapshot[0].clientId;

    const invalid = rejectionOf(() =>
      create({ body: { ...valid, clientId: taken, sessionBound: null } }),
    );
    expect(invalid.status).toBe(400);

    const duplicate = rejectionOf(() => create({ body: { ...valid, clientId: taken } }));
    expect(duplicate.status).toBe(409);
    expect(duplicate.error.code).toBe('OpenApp:ClientIdTaken');
  });

  it('checks the field errors before looking up the application on update', () => {
    const body = { ...valid, applicationType: 'spa' };

    const invalid = rejectionOf(() => update({ params: { id: 'app_missing' }, body }));
    expect(invalid.status).toBe(400);
    expect(fieldsOf(invalid)).toEqual(['applicationType']);

    const missing = rejectionOf(() => update({ params: { id: 'app_missing' }, body: valid }));
    expect(missing.status).toBe(404);
    expect(missing.error.code).toBe('OpenApp:NotFound');
  });

  it('deletes idempotently: a missing application succeeds and changes nothing', () => {
    expect(() => remove({ params: { id: 'missing-client' } })).not.toThrow();

    expect(OPEN_APPLICATIONS).toEqual(snapshot);
  });
});
