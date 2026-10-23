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

//#if (ExternalLogin)
/** 外部登录提供方标识与设置名中段的对应，按标识排序（与真实后端状态端点的顺序一致）。 */
const EXTERNAL_LOGIN_PROVIDERS = [
  { provider: 'github', key: 'Github' },
  { provider: 'google', key: 'Google' },
] as const;

/**
 * Mock 的演示部署基线（对应真实部署的 `ExternalAuth` 配置节）：两个提供方都启用、凭据齐全，
 * 设置页、登录入口与状态部件三处一致。生产基线两个提供方都是关闭的。
 * 假密钥只在这里出现：与真实后端一样，部署提供的密钥不作为默认值下发，任何 Mock 端点都不返回它。
 */
const EXTERNAL_LOGIN_DEPLOYMENT: Readonly<Record<string, string>> = {
  'ExternalLogin.Github.Enabled': 'true',
  'ExternalLogin.Github.ClientId': 'mock-github-client-id',
  'ExternalLogin.Github.ClientSecret': 'mock-github-client-secret',
  'ExternalLogin.Google.Enabled': 'true',
  'ExternalLogin.Google.ClientId': 'mock-google-client-id.apps.googleusercontent.com',
  'ExternalLogin.Google.ClientSecret': 'mock-google-client-secret',
};

/** 外部登录的一项设置定义：宿主级，非机密项以部署基线为默认值。 */
function externalLoginSetting(
  name: string,
  displayName: string,
  kind: 'boolean' | 'text' | 'secret',
): MockSettingDefinition {
  return {
    name,
    displayName,
    group: 'ExternalLogin',
    defaultValue: kind === 'secret' ? null : (EXTERNAL_LOGIN_DEPLOYMENT[name] ?? null),
    allowsTenantScope: false,
    allowsUserScope: false,
    allowsHostScope: true,
    ...(kind === 'boolean' ? { isBoolean: true } : {}),
    ...(kind === 'secret' ? { isSecret: true } : {}),
  };
}

//#endif
export const SETTING_DEFINITIONS: MockSettingDefinition[] = [
  //#if (IncludeLocalization)
  {
    name: 'Display.Language',
    displayName: 'Language',
    group: 'Display',
    // 组织给默认、个人可覆盖；默认值即部署基线，留空为"跟随系统"（客户端按设备语言渲染）
    defaultValue: null,
    allowsTenantScope: true,
    allowsUserScope: true,
  },
  //#endif
  {
    name: 'Display.TimeZone',
    displayName: 'Time zone',
    group: 'Display',
    defaultValue: null,
    allowsTenantScope: true,
    allowsUserScope: true,
  },
  // 进程级：两个层级标记都是 false，只有 allowsHostScope。与真实后端一样，租户上下文下
  // 不下发这类设置，写入也被拒绝（见 api/setting.ts）。
  //#if (LocalIdentity)
  {
    name: 'Registration.Enabled',
    displayName: 'Allow self sign-up',
    group: 'Registration',
    defaultValue: 'true',
    allowsTenantScope: true,
    allowsUserScope: false,
    isBoolean: true,
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
    name: 'Security.AllowEmailTwoFactor',
    displayName: 'Allow email codes as a sign-in second step',
    group: 'Security',
    defaultValue: 'false',
    allowsTenantScope: true,
    allowsUserScope: false,
    isBoolean: true,
  },
  //#endif
  //#if (LocalIdentity)
  {
    name: 'Security.SessionIdleTimeoutDays',
    displayName: 'Sign out after this many idle days',
    group: 'Security',
    defaultValue: '7',
    allowsTenantScope: true,
    allowsUserScope: false,
    minimum: 1,
    maximum: 365,
  },
  //#endif
  //#if (ExternalLogin)
  externalLoginSetting('ExternalLogin.Github.Enabled', 'Enable GitHub sign-in', 'boolean'),
  externalLoginSetting('ExternalLogin.Github.ClientId', 'GitHub client ID', 'text'),
  externalLoginSetting('ExternalLogin.Github.ClientSecret', 'GitHub client secret', 'secret'),
  externalLoginSetting('ExternalLogin.Google.Enabled', 'Enable Google sign-in', 'boolean'),
  externalLoginSetting('ExternalLogin.Google.ClientId', 'Google client ID', 'text'),
  externalLoginSetting('ExternalLogin.Google.ClientSecret', 'Google client secret', 'secret'),
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
    group: 'Retention',
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
    group: 'Retention',
    defaultValue: '365',
    allowsTenantScope: false,
    allowsUserScope: false,
    allowsHostScope: true,
    minimum: 30,
    maximum: 3650,
  },
  //#endif
  //#if (IncludeNotifications)
  {
    name: 'Notifications.RetentionEnabled',
    displayName: 'Clean up expired notifications',
    group: 'Retention',
    defaultValue: 'true',
    allowsTenantScope: false,
    allowsUserScope: false,
    allowsHostScope: true,
    isBoolean: true,
  },
  {
    name: 'Notifications.ReadRetentionDays',
    displayName: 'Read notification retention (days)',
    group: 'Retention',
    defaultValue: '90',
    allowsTenantScope: false,
    allowsUserScope: false,
    allowsHostScope: true,
    minimum: 1,
    maximum: 3650,
  },
  {
    name: 'Notifications.UnreadRetentionDays',
    displayName: 'Unread notification retention (days)',
    group: 'Retention',
    defaultValue: '365',
    allowsTenantScope: false,
    allowsUserScope: false,
    allowsHostScope: true,
    minimum: 1,
    maximum: 3650,
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
 * 例如 `Security` 显示为 "Sign-in & sessions"）。
 */
export const SETTING_GROUP_NAMES: Readonly<Record<string, string>> = {
  Display: 'Display',
  Operations: 'Operations',
  //#if (IncludeOperationRecords || IncludeNotifications)
  Retention: 'Data retention',
  //#endif
  //#if (LocalIdentity)
  Registration: 'Registration',
  //#endif
  //#if (LocalIdentity)
  Security: 'Sign-in & sessions',
  //#endif
  //#if (Email)
  Email: 'Email',
  //#endif
  //#if (ExternalLogin)
  ExternalLogin: 'External sign-in',
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
  //#if (IncludeNotifications)
  'Setting:Notifications.RetentionEnabled': '清理到期通知',
  'Setting:Notifications.ReadRetentionDays': '已读通知保留天数',
  'Setting:Notifications.UnreadRetentionDays': '未读通知保留天数',
  //#endif
  //#if (LocalIdentity)
  'Setting:Registration.Enabled': '开放自助注册',
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
  'Setting:Security.AllowEmailTwoFactor': '允许用邮箱验证码作为登录第二步',
  //#endif
  //#if (LocalIdentity)
  'Setting:Security.SessionIdleTimeoutDays': '会话空闲多少天后退出',
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
  //#if (ExternalLogin)
  'Setting:ExternalLogin.Github.Enabled': '启用 GitHub 登录',
  'Setting:ExternalLogin.Github.ClientId': 'GitHub Client ID',
  'Setting:ExternalLogin.Github.ClientSecret': 'GitHub Client Secret',
  'Setting:ExternalLogin.Google.Enabled': '启用 Google 登录',
  'Setting:ExternalLogin.Google.ClientId': 'Google Client ID',
  'Setting:ExternalLogin.Google.ClientSecret': 'Google Client Secret',
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
  //#if (IncludeOperationRecords || IncludeNotifications)
  'SettingGroup:Retention': '数据保留',
  //#endif
  //#if (LocalIdentity)
  'SettingGroup:Registration': '注册',
  //#endif
  //#if (LocalIdentity)
  'SettingGroup:Security': '登录与会话',
  //#endif
  //#if (Email)
  'SettingGroup:Email': '邮件发送',
  //#endif
  //#if (ExternalLogin)
  'SettingGroup:ExternalLogin': '外部登录',
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
//#if (ExternalLogin)

/** 一个外部登录提供方的有效配置。 */
export interface MockExternalLoginConfig {
  provider: string;
  enabled: boolean;
  clientId: string | null;
  clientSecret: string | null;
}

/**
 * 外部登录的有效配置：部署基线叠加宿主设置覆盖（宿主那一行，不是任何租户的覆盖），与真实后端的配置源顺序一致。
 * 提供方目录、绑定列表与状态端点都从这里判断，三处不会说法不一。
 */
export function externalLoginConfigs(): MockExternalLoginConfig[] {
  return EXTERNAL_LOGIN_PROVIDERS.map(({ provider, key }) => {
    // 清除覆盖（写 null）即删掉宿主那一行，读取回落到部署基线
    const value = (field: string): string | null => {
      const name = `ExternalLogin.${key}.${field}`;
      return TENANT_SETTING_VALUES.get(`host:${name}`) ?? EXTERNAL_LOGIN_DEPLOYMENT[name] ?? null;
    };
    return {
      provider,
      enabled: value('Enabled') === 'true',
      clientId: value('ClientId'),
      clientSecret: value('ClientSecret'),
    };
  });
}

/**
 * 凭据是否算"有"：与后端的 `string.IsNullOrWhiteSpace` 一致，只有空白也算没有。
 * 只用于判定，不改写存储的值。
 */
export function hasExternalLoginCredential(value: string | null): boolean {
  return value !== null && value.trim().length > 0;
}

/** 可用 = 已启用且两项凭据都有，真实后端据此登记认证方案。 */
export function isExternalLoginAvailable(config: MockExternalLoginConfig): boolean {
  return (
    config.enabled &&
    hasExternalLoginCredential(config.clientId) &&
    hasExternalLoginCredential(config.clientSecret)
  );
}

/** 当前可用的提供方标识，按标识排序：登录入口与绑定列表读这一份。 */
export function availableExternalProviders(): string[] {
  return externalLoginConfigs()
    .filter(isExternalLoginAvailable)
    .map((config) => config.provider);
}
//#endif
