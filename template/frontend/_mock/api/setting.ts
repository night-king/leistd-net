import { hasPermission, requirePermission } from './authorization';
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

/** 系统设置分组 → 管理它所需的子权限，与后端 SettingGroupPermissions 一致。 */
const GROUP_PERMISSIONS: Readonly<Record<string, string>> = {
  Display: PERMISSIONS.settings.display,
  //#if (LocalIdentity)
  Registration: PERMISSIONS.settings.registration,
  Security: PERMISSIONS.settings.security,
  //#if (ExternalLogin)
  ExternalLogin: PERMISSIONS.settings.externalLogin,
  //#endif
  //#if (Email)
  Email: PERMISSIONS.settings.email,
  //#endif
  //#endif
  Operations: PERMISSIONS.settings.operations,
  //#if (IncludeOperationRecords || IncludeNotifications)
  Retention: PERMISSIONS.settings.retention,
  //#endif
};

/**
 * 侧别为 Host 的子权限（只含进程级设置的分组）：租户上下文下权限检查本身就拒绝，与授予无关。
 * Mock 的授予不分侧别，这里补上这一层。
 */
// prettier-ignore
const HOST_SIDE_PERMISSIONS: ReadonlySet<string> = new Set([
  //#if (ExternalLogin)
  PERMISSIONS.settings.externalLogin,
  //#endif
  //#if (Email)
  PERMISSIONS.settings.email,
  //#endif
  PERMISSIONS.settings.operations,
  //#if (IncludeOperationRecords || IncludeNotifications)
  PERMISSIONS.settings.retention,
  //#endif
]);

function holds(permission: string): boolean {
  return (
    (!HOST_SIDE_PERMISSIONS.has(permission) || getMockSessionTenantKey() === 'host') &&
    hasPermission(permission)
  );
}

/** 端点级 403（未登录为 401）：分组子权限缺失，或 Host 侧子权限在租户上下文下；与授权管道一致，不带业务码。 */
function requireHeld(permission: string): void {
  if (!holds(permission)) {
    throw new MockException(403, { message: `Missing permission: ${permission}` });
  }
}

/** 与后端端点同一判据：能写租户级或进程级、持有入口权限，且持有该分组登记的子权限。 */
function canManage(definition: MockSettingDefinition): boolean {
  const groupPermission = GROUP_PERMISSIONS[definition.group];
  return (
    (definition.allowsTenantScope || (definition.allowsHostScope ?? false)) &&
    hasPermission(PERMISSIONS.settings.default) &&
    (groupPermission === undefined || holds(groupPermission))
  );
}

/** 写租户级或进程级值前的授权：入口权限之外，再要该分组的子权限。 */
function requireManage(definition: MockSettingDefinition): void {
  const groupPermission = GROUP_PERMISSIONS[definition.group];
  if (groupPermission !== undefined) {
    requireHeld(groupPermission);
  }
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

// 层级与后端设置组件一致：定义不允许的层写不进去。放行的话，Mock 里能存进组织值的设置到了真实后端一律 400
function assertScopeAllowed(definition: MockSettingDefinition, scope: 'tenant' | 'user'): void {
  const allowed =
    scope === 'user'
      ? definition.allowsUserScope
      : definition.allowsTenantScope || (definition.allowsHostScope ?? false);
  if (!allowed) {
    throw new MockException(400, {
      code: 'Setting:ScopeNotAllowed',
      message: `'${definition.displayName}' cannot be changed at this level.`,
    });
  }
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

//#if (IncludeNotifications)
const READ_RETENTION = 'Notifications.ReadRetentionDays';
const UNREAD_RETENTION = 'Notifications.UnreadRetentionDays';

/**
 * 未读通知的保留天数不得短于已读的，与后端校验器一致：写入与清除（候选值是回落后的默认值）都判，
 * 另一项取库里的覆盖值，没有就是默认值。
 */
function assertRetentionOrder(tenantKey: string, name: string, value: string | null): void {
  if (name !== READ_RETENTION && name !== UNREAD_RETENTION) {
    return;
  }
  // 本次写入的那一项取候选值（清除即默认值），另一项取库里的覆盖值，没有就是默认值
  const effective = (setting: string) =>
    Number(
      setting === name
        ? (value ?? requireDefinition(setting).defaultValue)
        : (TENANT_SETTING_VALUES.get(`${tenantKey}:${setting}`) ??
            requireDefinition(setting).defaultValue),
    );
  const read = effective(READ_RETENTION);
  const unread = effective(UNREAD_RETENTION);
  if (unread < read) {
    throw new MockException(400, {
      code: 'AppSetting:NotificationRetentionOrder',
      message: `Unread notifications must be kept at least as long as read ones (${unread} < ${read} days).`,
    });
  }
}

//#endif
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
  // 子权限侧别是 Host：租户上下文下由权限检查拒绝
  requireHeld(PERMISSIONS.settings.externalLogin);

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
    // 管理值只给管理者：不能管理、又不允许用户覆盖的设置不下发
    return visibleDefinitions(tenantKey)
      .map((definition) => ({ definition, manageable: canManage(definition) }))
      .filter(({ definition, manageable }) => manageable || definition.allowsUserScope)
      .map(({ definition, manageable }) => ({
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
        canManage: manageable,
      }));
  },

  'PUT /api/v1/settings/current-user': (req: MockRequest) => {
    const subjectId = requireSubjectId();
    const { name, value } = readBody(req);
    const definition = requireDefinition(name);
    assertScopeAllowed(definition, 'user');
    assertValidValue(definition, value);
    return write(USER_SETTING_VALUES, `${getMockSessionTenantKey()}:${subjectId}:${name}`, value);
  },

  'PUT /api/v1/settings/current-tenant': (req: MockRequest) => {
    // 改系统默认值需要管理权限（入口 + 分组子权限）；改自己的偏好只要登录。这层差异是两个端点存在的理由。
    requirePermission(PERMISSIONS.settings.default);
    const { name, value } = readBody(req);
    const definition = requireDefinition(name);
    requireManage(definition);
    assertScopeAllowed(definition, 'tenant');
    const tenantKey = getMockSessionTenantKey();
    // 进程级设置只有宿主那一行：租户写不进去，否则会存成该租户自己的覆盖
    if (definition.allowsHostScope && tenantKey !== 'host') {
      throw new MockException(403, {
        code: 'Setting:HostOnly',
        message: `Setting '${definition.displayName}' is process-wide and can only be changed on the host.`,
      });
    }
    assertValidValue(definition, value);
    //#if (IncludeNotifications)
    assertRetentionOrder(tenantKey, name, value);
    //#endif
    return write(TENANT_SETTING_VALUES, `${tenantKey}:${name}`, value);
  },
  //#if (Email)

  // mock 下不真的发信，只演示界面
  'POST /api/v1/settings/email/test': () => {
    requireHeld(PERMISSIONS.settings.email);
    return 'ok';
  },
  //#endif
  //#if (ExternalLogin)

  'GET /api/v1/settings/external-login/status': () => getExternalLoginStatus(),
  //#endif
};
