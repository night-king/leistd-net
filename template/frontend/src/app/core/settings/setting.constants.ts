/** 设置名常量，与后端 `SettingConstant` 逐字一致。 */
export const SETTINGS = {
  display: {
    //#if (IncludeLocalization)
    language: 'Display.Language',
    //#endif
    timeZone: 'Display.TimeZone',
  },
  logging: {
    minimumLevel: 'Logging.MinimumLevel',
    requestLevel: 'Logging.RequestLevel',
  },
} as const;
