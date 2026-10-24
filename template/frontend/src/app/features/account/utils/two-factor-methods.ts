import { TwoFactorStatusOutputDto } from '../../../shared/dtos/account-security.dto';
import { TwoFactorMethod } from '../../../shared/dtos/auth.dto';

/**
 * 登录第二步此刻能直接给出的方式（不含恢复码），按系统顺序：通行密钥（部署配了依赖方、本人登记过）、
 * 身份验证器、邮箱验证码（开启且此刻能用）。个人设置各节据此判断"默认"有没有可选的余地。
 */
export function usableTwoFactorMethods(status: TwoFactorStatusOutputDto | null): TwoFactorMethod[] {
  if (!status) {
    return [];
  }
  const methods: TwoFactorMethod[] = [];
  if (status.passkeysAvailable && status.passkeys.length > 0) {
    methods.push('passkey');
  }
  if (status.enabled) {
    methods.push('authenticator');
  }
  //#if (Email)
  if (status.emailEnabled && !status.emailUnavailableReason) {
    methods.push('email');
  }
  //#endif
  return methods;
}

/** 这种方式能用，而且还有别的方式可选：此时才标"默认"或给"设为默认"。 */
export function canChooseDefault(
  status: TwoFactorStatusOutputDto | null,
  method: TwoFactorMethod,
): boolean {
  const methods = usableTwoFactorMethods(status);
  return methods.length > 1 && methods.includes(method);
}
