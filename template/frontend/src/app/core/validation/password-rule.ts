//#if (LocalIdentity)
/**
 * 口令规则的唯一定义处，只做快速反馈；安全不变量在服务端 `PasswordPolicy`，两者必须一致。
 * 上下限分成两个校验器，提示能说明错在哪一头；规则选长度而不选复杂度。
 */
export const PASSWORD_MIN_LENGTH = 12;
export const PASSWORD_MAX_LENGTH = 256;
//#endif
