/**
 * 账户偏好契约：放在 `shared/`，`core` 的会话偏好状态、设置页与 Mock 共用。
 * 偏好由身份服务持有并计算，资源服务转交；三种部署形态同一形状。
 */

/** 外观取值，与后端 `SettingConstant.Display.ThemeModes` 一致。 */
export type AccountThemeMode = 'system' | 'light' | 'dark';

/**
 * 一份偏好：字段缺省或为 null 都表示已知未设置（语言、时区按设备回落，外观按跟随系统）。
 * 后端序列化忽略 null，未设置的字段不出现，所以字段都是可选的。
 */
export interface AccountPreferencesDto {
  language?: string | null;
  timeZone?: string | null;
  theme?: AccountThemeMode | null;
}

/** 可单独写入的偏好字段。 */
export type AccountPreferenceField = keyof AccountPreferencesDto;

/**
 * `GET /auth/me` 携带的偏好部分与 `PATCH /auth/me/preferences` 的响应：生效值与个人覆盖原值成对出现。
 * `/me` 上两者缺省（或为 null）表示暂时读不到（整份未知），不是"未设置"。
 */
export interface AccountPreferencesOutputDto {
  preferences?: AccountPreferencesDto | null;
  preferenceOverrides?: AccountPreferencesDto | null;
}
