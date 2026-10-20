import { HttpErrorResponse } from '@angular/common/http';

import { applicationErrorMessage, ApplicationHttpError } from './application-http-error';

function errorOf(body: unknown, status = 400): ApplicationHttpError {
  return ApplicationHttpError.from(
    new HttpErrorResponse({ error: body, status, statusText: 'Bad Request', url: '/api/v1/roles' }),
  );
}

describe('ApplicationHttpError', () => {
  it('builds the message from field errors, not the generic title, on validation failure', () => {
    const error = errorOf({
      type: 'urn:leistd:problem:validation-error',
      title: 'One or more validation errors occurred.',
      errors: [{ field: 'name', detail: '角色名称只能包含字母、数字和下划线' }],
    });

    expect(error.message).toBe('角色名称只能包含字母、数字和下划线');
  });

  // 显式 422 的 detail 同样只是概括
  it('prefers field errors over the detail of an explicit 422', () => {
    const error = errorOf(
      {
        title: '无法处理的实体',
        detail: '提交的信息有误。',
        errors: [{ field: 'phone', detail: '号码已被占用' }],
      },
      422,
    );

    expect(error.message).toBe('号码已被占用');
  });

  it('takes the first error per field and puts each field on its own line', () => {
    const error = errorOf({
      title: '错误请求',
      errors: [
        { field: 'name', detail: '角色名称不能为空' },
        { field: 'name', detail: '角色名称长度必须在 2-64 个字符之间' },
        { field: 'displayName', detail: '显示名称不能为空' },
      ],
    });

    expect(error.message).toBe('角色名称不能为空\n显示名称不能为空');
    expect(error.details.map((item) => item.field)).toEqual(['name', 'name', 'displayName']);
  });

  it('hides the raw browser exception text when no response was received', () => {
    const response = new HttpErrorResponse({
      error: new TypeError('Failed to fetch'),
      status: 0,
      url: '/api/v1/auth/end-impersonation',
    });

    const localized = ApplicationHttpError.from(response, {
      method: 'GET',
      networkErrorMessage: '无法连接到服务器',
    });
    expect(localized.message).toBe('无法连接到服务器');
    expect(localized.category).toBe('network');
    expect(ApplicationHttpError.from(response).message).not.toContain('Failed to fetch');
  });

  it('falls back to detail when there are no field errors', () => {
    const error = errorOf(
      { title: '冲突', detail: '角色名称已存在', code: 'Role:NameExists' },
      409,
    );

    expect(error.message).toBe('角色名称已存在');
    expect(error.code).toBe('Role:NameExists');
  });

  // 协议层失败只有状态码语义：没有 detail 与业务码，文案取本地化标题
  it('keeps the traceId on 5xx and builds a reportable message', () => {
    const error = errorOf(
      {
        title: '服务器内部错误',
        status: 500,
        traceId: '4bf92f3577b34da6a3ce929d0e0e4736',
      },
      500,
    );

    expect(error.code).toBeUndefined();
    expect(error.traceId).toBe('4bf92f3577b34da6a3ce929d0e0e4736');
    expect(applicationErrorMessage(error)).toBe(
      '服务器内部错误 (Trace ID: 4bf92f3577b34da6a3ce929d0e0e4736)',
    );
  });

  it('splits dictionary-shaped errors per message and uses the first per field', () => {
    const error = errorOf(
      {
        title: 'One or more validation errors occurred.',
        errors: {
          Name: ['The Name field is required.', 'Name is too short.'],
          Quantity: ['Must be positive.'],
        },
        traceId: '00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01',
      },
      400,
    );

    expect(error.details.map((item) => [item.field, item.detail])).toEqual([
      ['Name', 'The Name field is required.'],
      ['Name', 'Name is too short.'],
      ['Quantity', 'Must be positive.'],
    ]);
    expect(error.message).toBe('The Name field is required.\nMust be positive.');
    expect(error.code).toBeUndefined();
  });

  it('ignores errorCode and message from the response envelope', () => {
    const error = errorOf(
      { code: 409, errorCode: 'Role:NameExists', message: '角色名称已存在' },
      409,
    );

    expect(error.code).toBeUndefined();
    expect(error.message).not.toBe('角色名称已存在');
  });

  it('takes detail from a valid problem and reports the problem category', () => {
    const error = errorOf({ title: 'Conflict', detail: 'Role name already exists.' }, 409);

    expect(error.category).toBe('problem');
    expect(error.message).toBe('Role name already exists.');
  });

  // Angular 把空 statusText 补成 "OK"：HTTP/2 下空正文的 502 不能把它当成文案
  it('never shows the status text', () => {
    const error = ApplicationHttpError.from(
      new HttpErrorResponse({ status: 502, statusText: 'OK', url: '/api/v1/roles' }),
      { method: 'GET' },
    );

    expect(error.category).toBe('server');
    expect(error.message).not.toContain('OK');
    expect(error.message).toBe(
      'An unexpected error occurred. Please refresh the page and try again.',
    );
  });

  it('never shows a string or HTML response body', () => {
    const html = '<html><body><h1>502 Bad Gateway</h1><p>nginx</p></body></html>';
    const gateway = ApplicationHttpError.from(
      new HttpErrorResponse({ error: html, status: 502, statusText: 'Bad Gateway' }),
      { method: 'GET', serverErrorMessage: '服务暂时不可用' },
    );
    const plain = errorOf('Internal stack trace at Foo.Bar()', 400);

    expect(gateway.category).toBe('server');
    expect(gateway.message).toBe('服务暂时不可用');
    expect(plain.category).toBe('invalidResponse');
    expect(plain.message).not.toContain('stack trace');
  });

  it('ignores problem members that are not non-empty strings', () => {
    const error = errorOf({ title: '  ', detail: 42, errors: [{ field: 'name', detail: '' }] });

    expect(error.category).toBe('invalidResponse');
    expect(error.details).toEqual([]);
  });

  it('drops a malformed code or traceId', () => {
    const error = errorOf(
      { detail: 'Rejected.', code: 'bad code<script>', traceId: 'trace id with spaces' },
      400,
    );

    expect(error.category).toBe('problem');
    expect(error.code).toBeUndefined();
    expect(error.traceId).toBeUndefined();
  });

  it('classifies a 5xx without a problem on a safe method as server and keeps the traceId', () => {
    const error = ApplicationHttpError.from(
      new HttpErrorResponse({ error: { traceId: 'trace-9' }, status: 500 }),
      { method: 'GET' },
    );

    expect(error.category).toBe('server');
    expect(applicationErrorMessage(error)).toBe(
      'An unexpected error occurred. Please refresh the page and try again. (Trace ID: trace-9)',
    );
  });

  // 写请求遇到 5xx：服务端可能已经提交，提示先核实，不能让用户直接重复提交
  it('classifies a 5xx on a write request as uncertain, even with a problem body', () => {
    const error = ApplicationHttpError.from(
      new HttpErrorResponse({
        error: { title: 'Internal Server Error', traceId: 'trace-7' },
        status: 500,
      }),
      { method: 'POST', uncertainResultMessage: '操作可能已经完成' },
    );

    expect(error.category).toBe('uncertain');
    expect(error.message).toBe('操作可能已经完成');
    expect(applicationErrorMessage(error)).toBe('操作可能已经完成 (Trace ID: trace-7)');
  });

  it('classifies a write request without any response as uncertain', () => {
    const error = ApplicationHttpError.from(
      new HttpErrorResponse({ error: new ProgressEvent('error'), status: 0 }),
      { method: 'POST' },
    );

    expect(error.category).toBe('uncertain');
    expect(error.message).toBe(
      'The operation may have completed. Check the result before trying again.',
    );
  });

  // 2xx 进入错误流只可能是响应体解析失败：请求本身已经被处理
  it('classifies a 2xx parse failure as uncertain on writes and invalidResponse on reads', () => {
    const parseFailure = () =>
      new HttpErrorResponse({
        error: { error: new SyntaxError('Unexpected token <'), text: '<html></html>' },
        status: 200,
        statusText: 'OK',
      });

    const write = ApplicationHttpError.from(parseFailure(), { method: 'POST' });
    const read = ApplicationHttpError.from(parseFailure(), {
      method: 'GET',
      invalidResponseMessage: '无法处理服务器的响应',
    });

    expect(write.category).toBe('uncertain');
    expect(read.category).toBe('invalidResponse');
    expect(read.message).toBe('无法处理服务器的响应');
    expect(`${write.message} ${read.message}`).not.toContain('OK');
  });

  it('falls back to the request-failed text for a problem that only carries a code', () => {
    const error = errorOf({ code: 'Auth:ReauthenticationRequired' }, 400);

    expect(error.category).toBe('problem');
    expect(error.code).toBe('Auth:ReauthenticationRequired');
    expect(error.message).toBe('The request could not be completed.');
  });
});
