import { requirePermission } from './authorization';
//#if (ExternalLogin)
import { ExternalLoginStatusOutputDto } from '../../src/app/core/settings/setting.dto';
//#endif
import { PERMISSIONS } from '../../src/app/shared/constants/permission.constants';
import { MockException, MockRequest } from '../core/models';
// prettier-ignore
import {
  //#if (ExternalLogin)
  externalLoginConfigs,
  hasExternalLoginCredential,
  isExternalLoginAvailable,
  //#endif
  MockSettingDefinition,
  SETTING_DEFINITIONS,
  SETTING_GROUP_NAMES,
  //#if (IncludeLocalization)
  SETTING_TEXTS_ZH_CN,
  //#endif
  TENANT_SETTING_VALUES,
  USER_SETTING_VALUES,
} from '../data/settings';
import {
  getCurrentUser,
  getMockSessionSubjectId,
  getMockSessionTenantKey,
} from '../utils/current-user';
import { StoredMap } from '../utils/stored-map';

//#if (IncludeLocalization)
const SUPPORTED_LANGUAGES = ['en', 'zh-CN'];

/**
 * 显示名按请求语言给出，与真实后端一致：后端按 Accept-Language 查 `Setting:{name}` /
 * `SettingGroup:{group}` 词条，切换语言后设置页会重新取一次。请求头不是受支持语言时用英文。
 */
function localizedText(req: MockRequest, key: string, english: string): string {
  return req.headers.get('Accept-Language') === 'zh-CN'
    ? (SETTING_TEXTS_ZH_CN[key] ?? english)
    : english;
}

//#endif
// 租户取自会话而非租户提示头：真实后端优先用已认证主体的租户声明。键形状对齐真实 Store 的
// ScopeKey（`{tenant}:t` 与 `{tenant}:u:{userId}`）；用主体标识而非 Mock persona 做键，
// Resource 形态下 persona 是所有令牌共用的测试用户。
function requireSubjectId(): string {
  const subjectId = getCurrentUser() && getMockSessionSubjectId();
  if (!subjectId) {
    throw new MockException(401, { message: 'Not authenticated' });
  }
  return subjectId;
}

/** 本会话能看到的设置：与真实后端一样，进程级设置只在宿主上下文下发。 */
function visibleDefinitions(tenantKey: string): MockSettingDefinition[] {
  return tenantKey === 'host'
    ? SETTING_DEFINITIONS
    : SETTING_DEFINITIONS.filter((definition) => !definition.allowsHostScope);
}

// 顺序与后端设置组件的写入端一致：先确认设置存在且可见，再校验值域。
// 反过来的话，写一个不存在的设置名会先撞值域校验，返回 400 而不是 404。
function requireDefinition(name: string) {
  const definition = SETTING_DEFINITIONS.find((s) => s.name === name);
  if (!definition) {
    throw new MockException(404, {
      code: 'Setting:NotAvailable',
      message: `Setting '${name}' is not available.`,
    });
  }
  return definition;
}

// 值域与后端保持一致：Mock 放行了真实后端会拒的值，只会把问题推迟到联调时才发现。
function assertValidValue(definition: MockSettingDefinition, value: string | null): void {
  const name = definition.name;
  if (value === null) {
    return;
  }

  if (value.length === 0) {
    throw new MockException(400, {
      code: 'Setting:EmptyValueRejected',
      message: `An empty value is not accepted for '${name}'. Send null to clear the override.`,
    });
  }

  // 与设置组件一致：布尔设置只认小写的 true / false，`True`、`1` 都拒绝
  if (definition.isBoolean && value !== 'true' && value !== 'false') {
    throw new MockException(400, {
      code: 'Setting:BooleanRequired',
      message: `'${value}' is not a boolean; use 'true' or 'false'.`,
    });
  }

  //#if (IncludeLocalization)
  if (name === 'Display.Language' && !SUPPORTED_LANGUAGES.includes(value)) {
    throw new MockException(400, {
      code: 'Setting:ValueNotAllowed',
      message: `'${value}' is not a supported language.`,
    });
  }

  //#endif
  if (name === 'Display.TimeZone') {
    try {
      // 与后端的 HasIanaId 判定对齐：Windows 时区 ID 后端认、浏览器不认，两端都要拒。
      new Intl.DateTimeFormat('en-CA', { timeZone: value });
    } catch {
      throw new MockException(400, {
        code: 'AppSetting:TimeZoneInvalid',
        message: `'${value}' is not a valid IANA time zone id.`,
      });
    }
  }
}

function readBody(req: MockRequest): { name: string; value: string | null } {
  const { name, value } = (req.body ?? {}) as { name?: string; value?: string | null };
  if (typeof name !== 'string') {
    throw new MockException(400, { message: 'name is required.' });
  }
  return { name, value: value ?? null };
}

function write(map: StoredMap, key: string, value: string | null): null {
  // null 是唯一的清除语义：删除该层级的行，读取回落到下一层。
  if (value === null) {
    map.delete(key);
  } else {
    map.set(key, value);
  }
  return null;
}

//#if (ExternalLogin)
/**
 * 外部登录的生效状态：与提供方目录读同一份有效配置。只给布尔值，不回显也不截取密钥；
 * Mock 没有多实例，"本实例已生效"即可用。
 */
function getExternalLoginStatus(): ExternalLoginStatusOutputDto {
  requirePermission(PERMISSIONS.settings.default);
  // 外部登录是进程级配置，只有宿主能改，也只有宿主来看
  if (getMockSessionTenantKey() !== 'host') {
    throw new MockException(403, {
      code: 'AppSetting:ExternalLoginStatusHostOnly',
      message: 'The external sign-in status is only available on the host.',
    });
  }

  return {
    providers: externalLoginConfigs().map((config) => ({
      provider: config.provider,
      enabled: config.enabled,
      hasClientId: hasExternalLoginCredential(config.clientId),
      hasClientSecret: hasExternalLoginCredential(config.clientSecret),
      active: isExternalLoginAvailable(config),
      callbackUrl: `${location.origin}/api/v1/external-auth/${config.provider}/signin`,
    })),
  };
}

//#endif
/** 设置中心 Mock API（对应设置组件映射的 /api/v1/settings 端点）。 */
export const SETTING_API = {
  //#if (IncludeLocalization)
  'GET /api/v1/settings': (req: MockRequest) => {
  //#else
  'GET /api/v1/settings': () => {
  //#endif
    const subjectId = requireSubjectId();
    const tenantKey = getMockSessionTenantKey();
    return visibleDefinitions(tenantKey).map((definition) => ({
      name: definition.name,
      //#if (IncludeLocalization)
      displayName: localizedText(req, `Setting:${definition.name}`, definition.displayName),
      group: definition.group,
      groupDisplayName: localizedText(
        req,
        `SettingGroup:${definition.group}`,
        SETTING_GROUP_NAMES[definition.group] ?? definition.group,
      ),
      //#else
      displayName: definition.displayName,
      group: definition.group,
      groupDisplayName: SETTING_GROUP_NAMES[definition.group] ?? definition.group,
      //#endif
      // 机密设置与真实后端一致：一个值都不下发，只报告是否设过
      userValue: definition.isSecret
        ? null
        : (USER_SETTING_VALUES.get(`${tenantKey}:${subjectId}:${definition.name}`) ?? null),
      tenantValue: definition.isSecret
        ? null
        : (TENANT_SETTING_VALUES.get(`${tenantKey}:${definition.name}`) ?? null),
      defaultValue: definition.isSecret ? null : definition.defaultValue,
      allowsTenantScope: definition.allowsTenantScope,
      allowsUserScope: definition.allowsUserScope,
      allowsHostScope: definition.allowsHostScope ?? false,
      // 与真实后端一致：值为 null 的属性不下发，没有区间的设置不带这两个字段
      ...(definition.minimum === undefined ? {} : { minimum: definition.minimum }),
      ...(definition.maximum === undefined ? {} : { maximum: definition.maximum }),
      isBoolean: definition.isBoolean ?? false,
      isSecret: definition.isSecret ?? false,
      hasSecretValue:
        !!definition.isSecret && TENANT_SETTING_VALUES.has(`${tenantKey}:${definition.name}`),
    }));
  },

  'PUT /api/v1/settings/current-user': (req: MockRequest) => {
    const subjectId = requireSubjectId();
    const { name, value } = readBody(req);
    const definition = requireDefinition(name);
    assertValidValue(definition, value);
    return write(USER_SETTING_VALUES, `${getMockSessionTenantKey()}:${subjectId}:${name}`, value);
  },

  'PUT /api/v1/settings/current-tenant': (req: MockRequest) => {
    // 改系统默认值需要管理权限；改自己的偏好只要登录。这层差异是两个端点存在的理由。
    requirePermission(PERMISSIONS.settings.default);
    const { name, value } = readBody(req);
    const definition = requireDefinition(name);
    const tenantKey = getMockSessionTenantKey();
    // 进程级设置只有宿主那一行：租户写不进去，否则会存成该租户自己的覆盖
    if (definition.allowsHostScope && tenantKey !== 'host') {
      throw new MockException(403, {
        code: 'Setting:HostOnly',
        message: `Setting '${definition.displayName}' is process-wide and can only be changed on the host.`,
      });
    }
    assertValidValue(definition, value);
    return write(TENANT_SETTING_VALUES, `${tenantKey}:${name}`, value);
  },
  //#if (Email)

  // mock 下不真的发信，只演示界面
  'POST /api/v1/settings/email/test': () => {
    requirePermission(PERMISSIONS.settings.default);
    return 'ok';
  },
  //#endif
  //#if (ExternalLogin)

  'GET /api/v1/settings/external-login/status': () => getExternalLoginStatus(),
  //#endif
};
