//#if (IncludeLocalization)
import { SUPPORTED_LANGS } from '../../src/app/core/services/language-service';
//#endif
import { THEME_MODES } from '../../src/app/core/services/theme-service';
import {
  AccountPreferenceField,
  AccountPreferencesDto,
  AccountPreferencesOutputDto,
} from '../../src/app/shared/dtos/account-preferences.dto';
import { MockException, MockRequest } from '../core/models';
import { SETTING_DEFINITIONS, TENANT_SETTING_VALUES, USER_SETTING_VALUES } from '../data/settings';
import {
  getCurrentUser,
  getMockSessionSubjectId,
  getMockSessionTenantKey,
} from '../utils/current-user';

/** 偏好字段 → 持有方的设置名：个人覆盖与组织默认存在与设置页同一份数据里（键形状见 api/setting.ts）。 */
const SETTING_NAMES: Readonly<Record<AccountPreferenceField, string>> = {
  language: 'Display.Language',
  timeZone: 'Display.TimeZone',
  theme: 'Display.Theme',
};

/** 代码默认值取设置定义（与设置页同一份）：语言、时区留空即跟随设备，外观跟随系统。 */
function defaultOf(field: AccountPreferenceField): string | null {
  return (
    SETTING_DEFINITIONS.find((definition) => definition.name === SETTING_NAMES[field])
      ?.defaultValue ?? null
  );
}

/** 外观没有组织层：只有个人层与代码默认。 */
function organizationOf(tenantKey: string, field: AccountPreferenceField): string | null {
  return field === 'theme'
    ? null
    : (TENANT_SETTING_VALUES.get(`${tenantKey}:${SETTING_NAMES[field]}`) ?? null);
}

//#if (IncludeLocalization)
const FIELDS: readonly AccountPreferenceField[] = ['language', 'timeZone', 'theme'];
//#else
// 不含本地化的项目没有界面语言这一项，语言恒为 null
const FIELDS: readonly AccountPreferenceField[] = ['timeZone', 'theme'];
//#endif

/**
 * 当前会话主体的账户偏好，与真实后端同一回落：个人覆盖 → 组织默认 → 代码默认。
 * 没有主体时两份都为 null（暂时读不到），与后端"读不到偏好"的形状一致。
 */
export function currentAccountPreferences(): AccountPreferencesOutputDto {
  const subjectId = getCurrentUser() ? getMockSessionSubjectId() : null;
  if (!subjectId) {
    return { preferences: null, preferenceOverrides: null };
  }

  const tenantKey = getMockSessionTenantKey();
  const preferences: AccountPreferencesDto = { language: null, timeZone: null, theme: null };
  const overrides: AccountPreferencesDto = { language: null, timeZone: null, theme: null };
  for (const field of FIELDS) {
    const own =
      USER_SETTING_VALUES.get(`${tenantKey}:${subjectId}:${SETTING_NAMES[field]}`) ?? null;
    assign(overrides, field, own);
    assign(preferences, field, own ?? organizationOf(tenantKey, field) ?? defaultOf(field));
  }

  return { preferences, preferenceOverrides: overrides };
}

function assign(
  target: AccountPreferencesDto,
  field: AccountPreferenceField,
  value: string | null,
) {
  (target as unknown as Record<string, string | null>)[field] = value;
}

/** 与后端同一契约：合并补丁一次只写一个字段，`null` 清除个人覆盖；成功返回写入后的完整偏好。 */
function patchPreferences(req: MockRequest): AccountPreferencesOutputDto {
  const subjectId = getCurrentUser() ? getMockSessionSubjectId() : null;
  if (!subjectId) {
    throw new MockException(401, { message: 'Not authenticated' });
  }

  const body: unknown = req.body;
  const entries =
    body !== null && typeof body === 'object' && !Array.isArray(body)
      ? Object.entries(body as Record<string, unknown>)
      : [];
  // 请求体形状不对是结构校验失败（api.md §2.5）：400 字段错误、不带业务码，与后端一致
  if (entries.length !== 1) {
    throw invalidPatch('body', 'Send exactly one preference field.');
  }

  const [field, value] = entries[0];
  if (!(FIELDS as readonly string[]).includes(field)) {
    throw invalidPatch(field, `'${field}' is not a preference.`);
  }

  if (value !== null && typeof value !== 'string') {
    throw invalidPatch(field, `'${field}' must be a string or null.`);
  }

  if (value !== null) {
    assertValidValue(field as AccountPreferenceField, value);
  }

  const key = `${getMockSessionTenantKey()}:${subjectId}:${SETTING_NAMES[field as AccountPreferenceField]}`;
  if (value === null) {
    USER_SETTING_VALUES.delete(key);
  } else {
    USER_SETTING_VALUES.set(key, value);
  }

  return currentAccountPreferences();
}

function invalidPatch(field: string, detail: string): MockException {
  return new MockException(400, { errors: [{ field, detail }] });
}

// 值域与后端一致：Mock 放行了真实后端会拒的值，只会把问题推迟到联调时才发现。
// 空串与其他不认识的值一样按"不在允许列表"处理（时区按"不是合法 IANA 名"）。
function assertValidValue(field: AccountPreferenceField, value: string): void {
  //#if (IncludeLocalization)
  if (field === 'language') {
    assertAllowed(value, SUPPORTED_LANGS);
  }

  //#endif
  if (field === 'theme') {
    assertAllowed(value, THEME_MODES);
  }

  if (field === 'timeZone') {
    try {
      // 与后端的 HasIanaId 判定对齐：Windows 时区 ID 后端认、浏览器不认，两端都要拒。
      if (value.length === 0) {
        throw new RangeError('empty');
      }
      new Intl.DateTimeFormat('en-CA', { timeZone: value });
    } catch {
      throw new MockException(400, {
        code: 'AppSetting:TimeZoneInvalid',
        message: `'${value}' is not a valid IANA time zone id.`,
        data: { Value: value },
      });
    }
  }
}

function assertAllowed(value: string, allowed: readonly string[]): void {
  if (!allowed.includes(value)) {
    throw new MockException(400, {
      code: 'Setting:ValueNotAllowed',
      message: `'${value}' is not one of the allowed values.`,
      data: { Value: value, Allowed: [...allowed] },
    });
  }
}

export const ACCOUNT_PREFERENCES_API = {
  'PATCH /api/v1/auth/me/preferences': (req: MockRequest) => patchPreferences(req),
};
