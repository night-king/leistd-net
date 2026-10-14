import { StoredMap } from '../utils/stored-map';

/** 设置的 Mock 数据。覆盖值按主体键控，键形状对齐真实 Store 的 `ScopeKey`，用户级也带租户。 */
export interface MockSettingDefinition {
  name: string;
  /** 显示名，取后端 `Setting:{name}` 的英文词条；其他语言见下方对照表。 */
  displayName: string;
  /** 分组标识；设置页左侧按它分类。真实后端会把未分组的归入 `Other`。 */
  group: string;
  defaultValue: string | null;
  allowsTenantScope: boolean;
  allowsUserScope: boolean;
  /** 进程级设置：只有宿主那一份，租户与用户都不能覆盖。 */
  allowsHostScope?: boolean;
  /** 数值型设置的取值区间；界面据此渲染带上下界的数字输入框。 */
  minimum?: number;
  maximum?: number;
  /** 布尔型设置，渲染成开关（与后端设置定义的 `AsBoolean()` 对应）。 */
  isBoolean?: boolean;
  /** 机密设置：值不下发，只报告是否设过。 */
  isSecret?: boolean;
}

export const SETTING_DEFINITIONS: MockSettingDefinition[] = [
  //#if (IncludeLocalization)
  {
    name: 'Display.Language',
    displayName: 'Language',
    group: 'Display',
    // 留空即"跟随系统"：客户端按浏览器语言渲染，租户没有默认值可给
    defaultValue: null,
    allowsTenantScope: false,
    allowsUserScope: true,
  },
  //#endif
  {
    name: 'Display.TimeZone',
    displayName: 'Time zone',
    group: 'Display',
    defaultValue: null,
    allowsTenantScope: false,
    allowsUserScope: true,
  },
  // 进程级：两个层级标记都是 false，只有 allowsHostScope。真实后端在租户上下文下
  // 根本不下发这类设置，Mock 只有宿主视角，因此照常列出。
  {
    name: 'Logging.MinimumLevel',
    displayName: 'Minimum log level',
    group: 'Operations',
    defaultValue: 'Information',
    allowsTenantScope: false,
    allowsUserScope: false,
    allowsHostScope: true,
  },
  {
    name: 'Logging.RequestLevel',
    displayName: 'Request log level',
    group: 'Operations',
    defaultValue: 'Information',
    allowsTenantScope: false,
    allowsUserScope: false,
    allowsHostScope: true,
  },
  //#if (IncludeOperationRecords)
  {
    name: 'Audit.RetentionEnabled',
    displayName: 'Archive expired operation records',
    group: 'Audit',
    defaultValue: 'false',
    allowsTenantScope: false,
    allowsUserScope: false,
    allowsHostScope: true,
    isBoolean: true,
  },
  //#endif
  //#if (IncludeOperationRecords)
  {
    name: 'Audit.RetentionDays',
    displayName: 'Operation record retention (days)',
    group: 'Audit',
    defaultValue: '365',
    allowsTenantScope: false,
    allowsUserScope: false,
    allowsHostScope: true,
    minimum: 30,
    maximum: 3650,
  },
  //#endif
  //#if (Email)
  {
    name: 'Registration.EnableEmailVerification',
    displayName: 'Require email verification',
    group: 'Registration',
    defaultValue: 'false',
    allowsTenantScope: true,
    allowsUserScope: false,
    isBoolean: true,
  },
  //#endif
  //#if (LocalIdentity)
  {
    name: 'Registration.CaptchaExpiryMinutes',
    displayName: 'Captcha lifetime (minutes)',
    group: 'Registration',
    defaultValue: '5',
    allowsTenantScope: true,
    allowsUserScope: false,
    minimum: 1,
    maximum: 60,
  },
  //#endif
  //#if (LocalIdentity)
  {
    name: 'Security.LockoutMaxFailedAttempts',
    displayName: 'Lock the account after this many failed sign-ins (0 = never)',
    group: 'Security',
    defaultValue: '5',
    allowsTenantScope: true,
    allowsUserScope: false,
    minimum: 0,
    maximum: 100,
  },
  //#endif
  //#if (LocalIdentity)
  {
    name: 'Security.LockoutDurationMinutes',
    displayName: 'Lockout duration (minutes)',
    group: 'Security',
    defaultValue: '15',
    allowsTenantScope: true,
    allowsUserScope: false,
    minimum: 1,
    maximum: 1440,
  },
  //#endif
  //#if (LocalIdentity)
  {
    name: 'Security.RequireTwoFactor',
    displayName: 'Require two-factor authentication for everyone',
    group: 'Security',
    defaultValue: 'false',
    allowsTenantScope: true,
    allowsUserScope: false,
    isBoolean: true,
  },
  //#endif
  //#if (Email)
  {
    name: 'Email.SmtpHost',
    displayName: 'SMTP host',
    group: 'Email',
    defaultValue: 'localhost',
    allowsTenantScope: false,
    allowsUserScope: false,
    allowsHostScope: true,
  },
  //#endif
  //#if (Email)
  {
    name: 'Email.SmtpPort',
    displayName: 'SMTP port',
    group: 'Email',
    defaultValue: '1025',
    allowsTenantScope: false,
    allowsUserScope: false,
    allowsHostScope: true,
    minimum: 1,
    maximum: 65535,
  },
  //#endif
  //#if (Email)
  {
    name: 'Email.SmtpEnableSsl',
    displayName: 'Use TLS',
    group: 'Email',
    defaultValue: 'false',
    allowsTenantScope: false,
    allowsUserScope: false,
    allowsHostScope: true,
    isBoolean: true,
  },
  //#endif
  //#if (Email)
  {
    name: 'Email.SmtpUsername',
    displayName: 'SMTP username',
    group: 'Email',
    defaultValue: null,
    allowsTenantScope: false,
    allowsUserScope: false,
    allowsHostScope: true,
  },
  //#endif
  //#if (Email)
  {
    name: 'Email.SmtpPassword',
    displayName: 'SMTP password',
    group: 'Email',
    defaultValue: null,
    allowsTenantScope: false,
    allowsUserScope: false,
    allowsHostScope: true,
    isSecret: true,
  },
  //#endif
  //#if (Email)
  {
    name: 'Email.DefaultFromAddress',
    displayName: 'Sender address',
    group: 'Email',
    defaultValue: 'noreply@example.com',
    allowsTenantScope: false,
    allowsUserScope: false,
    allowsHostScope: true,
  },
  //#endif
  //#if (Email)
  {
    name: 'Email.DefaultFromName',
    displayName: 'Sender name',
    group: 'Email',
    defaultValue: 'Template Project',
    allowsTenantScope: false,
    allowsUserScope: false,
    allowsHostScope: true,
  },
  //#endif
  //#if (LocalIdentity)
  {
    name: 'Notifications.Security.Email',
    displayName: 'Security alerts by email',
    group: 'Notifications',
    defaultValue: 'true',
    allowsTenantScope: false,
    allowsUserScope: true,
    isBoolean: true,
  },
  //#endif
  //#if (IncludeNotifications)
  {
    name: 'Notifications.System.InApp',
    displayName: 'System notifications in the app',
    group: 'Notifications',
    defaultValue: 'true',
    allowsTenantScope: false,
    allowsUserScope: true,
    isBoolean: true,
  },
  //#endif
  //#if (IncludeNotifications)
  {
    name: 'Notifications.System.Email',
    displayName: 'System notifications by email',
    group: 'Notifications',
    defaultValue: 'false',
    allowsTenantScope: false,
    allowsUserScope: true,
    isBoolean: true,
  },
  //#endif
];

/**
 * 分组显示名，与后端 `SettingGroup:{group}` 的英文词条一致（真实后端按它返回，不是回显分组标识：
 * 例如 `Security` 显示为 "Sign-in security"）。
 */
export const SETTING_GROUP_NAMES: Readonly<Record<string, string>> = {
  Display: 'Display',
  Operations: 'Operations',
  //#if (IncludeOperationRecords)
  Audit: 'Audit',
  //#endif
  //#if (LocalIdentity)
  Registration: 'Registration',
  //#endif
  //#if (LocalIdentity)
  Security: 'Sign-in security',
  //#endif
  //#if (Email)
  Email: 'Email',
  //#endif
  //#if (IncludeNotifications)
  Notifications: 'Notifications',
  //#endif
};
//#if (IncludeLocalization)

/**
 * zh-CN 下的显示名，键与后端资源同名（`Setting:{name}` / `SettingGroup:{group}`），值与后端 zh-CN 词条一致。
 * 后端改词条时这里同步改；没列到的键回落英文，与真实后端缺词条时的表现相同。
 */
// prettier-ignore
export const SETTING_TEXTS_ZH_CN: Readonly<Record<string, string>> = {
  'Setting:Display.Language': '界面语言',
  'Setting:Display.TimeZone': '时区',
  'Setting:Logging.MinimumLevel': '最小日志级别',
  'Setting:Logging.RequestLevel': '请求日志级别',
  //#if (IncludeOperationRecords)
  'Setting:Audit.RetentionEnabled': '到期操作记录搬入归档',
  //#endif
  //#if (IncludeOperationRecords)
  'Setting:Audit.RetentionDays': '操作记录保留天数',
  //#endif
  //#if (Email)
  'Setting:Registration.EnableEmailVerification': '要求邮箱验证',
  //#endif
  //#if (LocalIdentity)
  'Setting:Registration.CaptchaExpiryMinutes': '图形验证码有效期（分钟）',
  //#endif
  //#if (LocalIdentity)
  'Setting:Security.LockoutMaxFailedAttempts': '连续登录失败多少次后锁定（0 为不锁定）',
  //#endif
  //#if (LocalIdentity)
  'Setting:Security.LockoutDurationMinutes': '锁定时长（分钟）',
  //#endif
  //#if (LocalIdentity)
  'Setting:Security.RequireTwoFactor': '要求所有人启用两步验证',
  //#endif
  //#if (Email)
  'Setting:Email.SmtpHost': 'SMTP 主机',
  //#endif
  //#if (Email)
  'Setting:Email.SmtpPort': 'SMTP 端口',
  //#endif
  //#if (Email)
  'Setting:Email.SmtpEnableSsl': '启用 TLS 加密',
  //#endif
  //#if (Email)
  'Setting:Email.SmtpUsername': 'SMTP 用户名',
  //#endif
  //#if (Email)
  'Setting:Email.SmtpPassword': 'SMTP 口令',
  //#endif
  //#if (Email)
  'Setting:Email.DefaultFromAddress': '发件地址',
  //#endif
  //#if (Email)
  'Setting:Email.DefaultFromName': '发件人名称',
  //#endif
  //#if (LocalIdentity)
  'Setting:Notifications.Security.Email': '安全提醒同时发邮件',
  //#endif
  //#if (IncludeNotifications)
  'Setting:Notifications.System.InApp': '在站内接收系统通知',
  //#endif
  //#if (IncludeNotifications)
  'Setting:Notifications.System.Email': '系统通知同时发邮件',
  //#endif
  'SettingGroup:Display': '显示',
  'SettingGroup:Operations': '运维',
  //#if (IncludeOperationRecords)
  'SettingGroup:Audit': '审计',
  //#endif
  //#if (LocalIdentity)
  'SettingGroup:Registration': '注册验证',
  //#endif
  //#if (LocalIdentity)
  'SettingGroup:Security': '登录安全',
  //#endif
  //#if (Email)
  'SettingGroup:Email': '邮件发送',
  //#endif
  //#if (IncludeNotifications)
  'SettingGroup:Notifications': '通知',
  //#endif
};
//#endif

/**
 * 用户级覆盖：`${tenantKey}:${subjectId}:${settingName}` → 值，宿主用 `host`。键用主体标识
 * （Resource 形态下是令牌的 `sub`），不是 Mock persona，否则同租户的两个真实用户会串数据。
 * 与租户级覆盖一样存在 sessionStorage：后端把设置存进数据库，刷新后账户语言、时区不能丢。
 */
export const USER_SETTING_VALUES = new StoredMap('mock_user_settings');

/** 租户级覆盖：`${tenantKey}:${settingName}` → 值；宿主用 `host`。 */
export const TENANT_SETTING_VALUES = new StoredMap('mock_tenant_settings');
