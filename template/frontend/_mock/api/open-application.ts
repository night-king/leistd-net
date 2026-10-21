import { requirePermission } from './authorization';
import {
  CreateOpenApplicationInputDto,
  OpenApplicationOutputDto,
  UpdateOpenApplicationInputDto,
} from '../../src/app/features/platform/dtos/open-application.dto';
import { PERMISSIONS } from '../../src/app/shared/constants/permission.constants';
import { PagedResultDto } from '../../src/app/shared/dtos/paged-result.dto';
import { MockException, MockRequest } from '../core/models';
import { MockSortFields, sortMockRows } from '../core/sorting';
import {
  MockOpenApplication,
  OPEN_APPLICATION_SCOPES,
  OPEN_APPLICATIONS,
} from '../data/open-applications';

const applications = OPEN_APPLICATIONS;

function toOutput(item: MockOpenApplication): OpenApplicationOutputDto {
  const { clientSecret: _clientSecret, ...output } = item;
  return output;
}

function getQueryValue(value: unknown) {
  const normalized = Array.isArray(value) ? value[0] : value;
  return normalized === undefined || normalized === null || normalized === ''
    ? undefined
    : String(normalized);
}

const APPLICATION_SORT_FIELDS: MockSortFields<MockOpenApplication> = {
  clientId: (item) => item.clientId,
  displayName: (item) => item.displayName,
  creationTime: (item) => Date.parse(item.creationTime),
};

function sortApplications(items: MockOpenApplication[], sorting?: unknown) {
  // 内存 LINQ 升序 null 前置；用户和角色的 PostgreSQL 查询 null 后置。
  return sortMockRows(
    items,
    sorting,
    APPLICATION_SORT_FIELDS,
    'creationTime desc',
    'clientId',
    'first',
  );
}

function getOpenApplications(req: MockRequest) {
  requirePermission(PERMISSIONS.openApplications.default);
  const keyword = getQueryValue(req.queryParams['keyword']);
  const applicationType = getQueryValue(req.queryParams['applicationType']);
  const clientType = getQueryValue(req.queryParams['clientType']);
  const offset = getQueryValue(req.queryParams['offset']) ?? '0';
  const limit = getQueryValue(req.queryParams['limit']) ?? '10';
  const sorting = getQueryValue(req.queryParams['sorting']);
  let items = [...applications];

  if (keyword) {
    const value = keyword.trim().toLowerCase();
    items = items.filter(
      (item) =>
        item.clientId.toLowerCase().includes(value) ||
        (item.displayName ?? '').toLowerCase().includes(value),
    );
  }

  if (applicationType) {
    items = items.filter((item) => item.applicationType === applicationType);
  }

  if (clientType) {
    items = items.filter((item) => item.clientType === clientType);
  }

  items = sortApplications(items, sorting);

  const totalCount = items.length;
  const start = +offset;
  const end = start + +limit;

  return {
    totalCount,
    items: items.slice(start, end).map(toOutput),
  } as PagedResultDto<OpenApplicationOutputDto>;
}

function getOpenApplication(req: MockRequest) {
  requirePermission(PERMISSIONS.openApplications.default);
  const id = req.params['id'];
  const application = applications.find((item: MockOpenApplication) => item.id === id);
  if (!application) {
    throw new MockException(404, {
      code: 'OpenApp:NotFound',
      message: 'Open application not found',
    });
  }
  return toOutput(application);
}

const CALLBACK_URI_INVALID =
  'Each callback URI must be an absolute URI without whitespace or a fragment.';

/** 与后端回调地址校验同一判据：绝对 URI、不含空白与片段。 */
function isValidCallbackUri(value: string): boolean {
  if (!value?.trim() || /\s/.test(value)) {
    return false;
  }
  try {
    return new URL(value).hash === '';
  } catch {
    return false;
  }
}

// 与后端一致：入参校验先于查找与业务规则（更新不存在的应用时，非法入参仍是 400），失败是 400 字段错误、不带业务码，一次报全
function validateInput(input: CreateOpenApplicationInputDto | UpdateOpenApplicationInputDto) {
  const errors: { field: string; detail: string }[] = [];
  const clientId = 'clientId' in input ? input.clientId.trim() : undefined;
  if (clientId === '') {
    errors.push({ field: 'clientId', detail: 'Client ID is required.' });
  }
  // 与后端 DTO 的必填与允许值同一口径
  const allowedValues = [
    ['applicationType', 'Application type', ['web', 'native', 'service']],
    ['clientType', 'Client type', ['public', 'confidential']],
  ] as const;
  for (const [field, name, allowed] of allowedValues) {
    const value: unknown = input[field];
    if (typeof value !== 'string' || value === '') {
      errors.push({ field, detail: `${name} is required.` });
    } else if (!(allowed as readonly string[]).includes(value)) {
      errors.push({ field, detail: `${name} is not an allowed value.` });
    }
  }
  // 会话绑定必须显式给值，缺失或 null 都不行
  if (typeof input.sessionBound !== 'boolean') {
    errors.push({ field: 'sessionBound', detail: 'Session bound is required.' });
  }
  for (const field of ['redirectUris', 'postLogoutRedirectUris'] as const) {
    if (!(input[field] ?? []).every(isValidCallbackUri)) {
      errors.push({ field, detail: CALLBACK_URI_INVALID });
    }
  }
  if (errors.length > 0) {
    throw new MockException(400, { errors });
  }
}

function validateBusinessRules(
  input: CreateOpenApplicationInputDto | UpdateOpenApplicationInputDto,
  id?: string,
) {
  const clientId = 'clientId' in input ? input.clientId.trim() : undefined;
  if (
    clientId &&
    applications.some((item: MockOpenApplication) => item.clientId === clientId && item.id !== id)
  ) {
    throw new MockException(409, {
      code: 'OpenApp:ClientIdTaken',
      message: `Client ID already exists: ${clientId}`,
    });
  }

  const exchange = 'gt:urn:ietf:params:oauth:grant-type:token-exchange';
  const userGrants = [
    'gt:authorization_code',
    'gt:refresh_token',
    'gt:implicit',
    'gt:password',
    'gt:device_code',
  ];
  if (
    input.permissions.includes(exchange) &&
    (input.clientType !== 'confidential' ||
      !input.permissions.includes('ept:token') ||
      input.permissions.some((permission) => userGrants.includes(permission)))
  ) {
    throw new MockException(400, {
      code: 'OpenApp:ExchangeClientInvalid',
      message:
        'Token Exchange requires a confidential service client with token permission and no user-facing grants.',
    });
  }
  for (const permission of input.permissions.filter((permission) =>
    permission.startsWith('aud:'),
  )) {
    if (!OPEN_APPLICATION_SCOPES.some((scope) => `aud:${scope.audience}` === permission)) {
      throw new MockException(400, {
        code: 'OpenApp:AudienceUnsupported',
        message: 'Unsupported audience.',
      });
    }
  }

  if (input.clientType === 'public' && 'clientSecret' in input && input.clientSecret) {
    throw new MockException(400, { message: 'Public clients cannot configure a client secret' });
  }

  if (
    (input.applicationType === 'native' || input.clientType === 'public') &&
    !input.requirements.includes('ft:pkce')
  ) {
    throw new MockException(400, {
      code: 'OpenApp:PkceRequired',
      message: 'Native/Public clients must enable PKCE',
    });
  }
}

function createOpenApplication(req: MockRequest) {
  requirePermission(PERMISSIONS.openApplications.create);
  const body = req.body as CreateOpenApplicationInputDto;
  validateInput(body);
  validateBusinessRules(body);

  const newApplication: MockOpenApplication = {
    id: body.clientId,
    clientId: body.clientId,
    displayName: body.displayName,
    applicationType: body.applicationType,
    clientType: body.clientType,
    redirectUris: body.redirectUris || [],
    postLogoutRedirectUris: body.postLogoutRedirectUris || [],
    permissions: body.permissions || [],
    requirements: body.requirements || [],
    settings: {},
    properties: {},
    hasClientSecret: body.clientType === 'confidential',
    sessionBound: body.sessionBound,
    creationTime: new Date().toISOString(),
    clientSecret:
      body.clientType === 'confidential' ? `mock-secret-${crypto.randomUUID()}` : undefined,
  };

  applications.unshift(newApplication);
  // 创建响应一次性返回明文 Secret（列表/详情仍通过 toOutput 隐藏），供前端弹窗展示。
  return { ...toOutput(newApplication), clientSecret: newApplication.clientSecret };
}

function updateOpenApplication(req: MockRequest) {
  requirePermission(PERMISSIONS.openApplications.update);
  const id = req.params['id'];
  const body = req.body as UpdateOpenApplicationInputDto;
  validateInput(body);
  const index = applications.findIndex((item: MockOpenApplication) => item.id === id);
  if (index === -1) {
    throw new MockException(404, {
      code: 'OpenApp:NotFound',
      message: 'Open application not found',
    });
  }

  validateBusinessRules(body, id);

  applications[index] = {
    ...applications[index],
    displayName: body.displayName,
    applicationType: body.applicationType,
    clientType: body.clientType,
    redirectUris: body.redirectUris || [],
    postLogoutRedirectUris: body.postLogoutRedirectUris || [],
    permissions: body.permissions || [],
    requirements: body.requirements || [],
    hasClientSecret: body.clientType === 'confidential' && applications[index].hasClientSecret,
    sessionBound: body.sessionBound,
    clientSecret: body.clientType === 'confidential' ? applications[index].clientSecret : undefined,
  };

  return toOutput(applications[index]);
}

function deleteOpenApplication(req: MockRequest) {
  requirePermission(PERMISSIONS.openApplications.delete);
  const id = req.params['id'];
  const index = applications.findIndex((item: MockOpenApplication) => item.id === id);
  if (index !== -1) {
    applications.splice(index, 1);
  }
  return { success: true };
}

function resetOpenApplicationSecret(req: MockRequest) {
  requirePermission(PERMISSIONS.openApplications.resetSecret);
  const id = req.params['id'];
  const application = applications.find((item: MockOpenApplication) => item.id === id);
  if (!application) {
    throw new MockException(404, {
      code: 'OpenApp:NotFound',
      message: 'Open application not found',
    });
  }
  if (application.clientType !== 'confidential') {
    throw new MockException(409, {
      code: 'OpenApp:SecretResetConfidentialOnly',
      message: 'Only confidential clients can reset their secret',
    });
  }

  const clientSecret = `mock_secret_${Math.random().toString(36).slice(2, 14)}`;
  application.clientSecret = clientSecret;
  application.hasClientSecret = true;
  return { clientSecret };
}

export const OPEN_APPLICATION_API = {
  'GET /api/v1/open-applications': (req: MockRequest) => getOpenApplications(req),
  'GET /api/v1/open-applications/scopes': () => OPEN_APPLICATION_SCOPES,
  'GET /api/v1/open-applications/:id': (req: MockRequest) => getOpenApplication(req),
  'POST /api/v1/open-applications': (req: MockRequest) => createOpenApplication(req),
  'PUT /api/v1/open-applications/:id': (req: MockRequest) => updateOpenApplication(req),
  'DELETE /api/v1/open-applications/:id': (req: MockRequest) => deleteOpenApplication(req),
  'POST /api/v1/open-applications/:id/reset-secret': (req: MockRequest) =>
    resetOpenApplicationSecret(req),
};
