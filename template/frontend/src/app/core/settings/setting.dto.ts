/** 一项设置在各层级上的覆盖值及其可覆盖层级。 */
export interface SettingOutputDto {
  /** 设置名称，跨前后端的字符串契约。 */
  name: string;
  /** 已按请求 culture 翻译的显示名称。 */
  displayName: string;
  /** 所属分组的稳定标识；界面按它分类，未分组的由后端归入 `Other`。 */
  group: string;
  /** 已按请求 culture 翻译的分组名称。 */
  groupDisplayName: string;
  /** 当前用户的个人覆盖值；`null` 表示未覆盖，继承租户默认值或代码默认值。 */
  userValue: string | null;
  /** 当前租户的默认覆盖值；`null` 表示未覆盖，继承代码默认值。 */
  tenantValue: string | null;
  /** 代码默认值。 */
  defaultValue: string | null;
  /** 是否允许写入租户级默认值。 */
  allowsTenantScope: boolean;
  /** 是否允许用户覆盖为个人偏好。 */
  allowsUserScope: boolean;
  /**
   * 是否是进程级设置（只有宿主一份）；为 `true` 时另两个层级标记都是 `false`，归到系统页。
   * 租户上下文下后端不下发这类设置。
   */
  allowsHostScope: boolean;
  /** 数值型设置的下界；非数值型不带这个字段。值域以服务端为准，这里只用于渲染控件。 */
  minimum?: number | null;
  /** 数值型设置的上界；非数值型不带这个字段。 */
  maximum?: number | null;
  /** 布尔型设置：值只能是 `'true'` / `'false'`，渲染成开关。 */
  isBoolean?: boolean;
  /** 机密设置（加密落库）：各层的值都不下发，渲染成只写的口令框。 */
  isSecret?: boolean;
  /** 机密设置在可写的那一层是否已经设过值。 */
  hasSecretValue?: boolean;
}

/** 写入一项设置；`value` 为 null 表示清除该层级，回落到下一层。 */
export interface SetSettingInputDto {
  name: string;
  value: string | null;
}
//#if (ExternalLogin)

/** 外部登录各提供方在本实例上的生效状态（宿主）。不含任何凭据内容。 */
export interface ExternalLoginStatusOutputDto {
  /** 按提供方标识排序。 */
  providers: ExternalLoginProviderStatusOutputDto[];
}

/** 一个提供方的生效状态。 */
export interface ExternalLoginProviderStatusOutputDto {
  /** 提供方标识（`github`、`google`）。 */
  provider: string;
  /** 开关是否打开（启用意图）。 */
  enabled: boolean;
  /** 是否配置了 Client ID（设置或部署配置）。 */
  hasClientId: boolean;
  /** 是否配置了 Client Secret（设置或部署配置）；机密设置不回显，设置页本身看不出部署提供的密钥。 */
  hasClientSecret: boolean;
  /** 本实例当前是否提供该入口；多实例在刷新周期内可能各不相同。 */
  active: boolean;
  /** 要登记到提供方的回调地址。 */
  callbackUrl: string;
}
//#endif
