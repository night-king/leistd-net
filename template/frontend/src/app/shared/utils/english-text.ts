/**
 * 不含本地化时组件取文案的函数：按键查组件自带的英文表，`{{name}}` 按参数替换。与 `*transloco="let t"`
 * 的 `t` 同名同签名，模板正文两种形态共用。英文表须与 `en.json` 一致（i18n 闸门校验），缺键原样返回。
 * 校验提示（`validation.*`）统一并入每张表，参数可以直接传校验错误对象。
 */
export function englishText(table: Readonly<Record<string, string>>) {
  const texts: Readonly<Record<string, string>> = { ...ENGLISH_VALIDATION, ...table };
  return (key: string, params?: object): string =>
    texts[key]?.replace(/\{\{\s*(\w+)\s*\}\}/g, (_, name: string) =>
      String((params as Record<string, unknown> | undefined)?.[name] ?? ''),
    ) ?? key;
}

/**
 * 校验提示的英文表，与 `en.json` 的 `validation` 段逐条一致；模板按错误类型拼键，新增错误类型时
 * 两边同时补。
 */
const ENGLISH_VALIDATION: Record<string, string> = {
  'validation.required': 'This field is required.',
  'validation.email': 'Please enter a valid email address.',
  'validation.minLength': 'Must be at least {{minLength}} characters.',
  'validation.maxLength': 'Must not exceed {{maxLength}} characters.',
  'validation.usernamePattern': 'Must be 3–64 letters, digits, or underscores.',
  'validation.roleNamePattern': 'Must be 2–64 letters, digits, or underscores.',
  'validation.phonePattern': 'Only digits, spaces, and + - ( ) are allowed.',
  'validation.passwordMismatch': 'The two passwords do not match.',
  'validation.passwordSameAsCurrent': 'The new password must differ from the current one.',
  'validation.tenantNamePattern':
    'Use letters, digits and hyphens only, not starting or ending with a hyphen, up to 63 characters.',
  'validation.connectionNamePattern':
    'Use lowercase letters, digits and hyphens only, 1 to 64 characters.',
  'validation.connectionNameTaken':
    'This connection name is already registered; change its connection string instead.',
  'validation.pkceRequired': 'Native/Public clients must enable PKCE.',
  'validation.credentialRejected': 'Not accepted. Check it and try again.',
};
