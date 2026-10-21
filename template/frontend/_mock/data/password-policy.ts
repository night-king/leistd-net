//#if (LocalIdentity)
import {
  PASSWORD_MAX_LENGTH,
  PASSWORD_MIN_LENGTH,
} from '../../src/app/core/validation/password-rule';
import { MockException } from '../core/models';

/**
 * 复刻后端 `PasswordPolicy` 的口令守卫：长度区间复用前端唯一定义，
 * 拒绝清单与错误消息在此镜像后端。后端策略调整时必须同步本文件，
 * 否则 mock 独立运行时的行为分叉只会在联调期暴露。
 */
const REJECTED_PASSWORDS = [
  'Admin@123456',
  'admin',
  'password',
  'Password1!',
  'P@ssw0rd',
  '123456789012',
];

/** 复刻后端 `PasswordPolicy.Ensure`：不满足策略即抛与真后端同形的 400。 */
export function ensureAcceptablePassword(password: unknown, subject: string): void {
  const problem = describePassword(typeof password === 'string' ? password : undefined);
  if (problem) {
    throw new MockException(400, { message: `${subject} ${problem}` });
  }
}

function describePassword(password: string | undefined): string | undefined {
  if (!password?.trim()) {
    return 'is required.';
  }
  if (password.length < PASSWORD_MIN_LENGTH) {
    return `must be at least ${PASSWORD_MIN_LENGTH} characters long.`;
  }
  if (password.length > PASSWORD_MAX_LENGTH) {
    return `must be at most ${PASSWORD_MAX_LENGTH} characters long.`;
  }
  if (REJECTED_PASSWORDS.some((rejected) => rejected.toLowerCase() === password.toLowerCase())) {
    return 'is a well-known or previously published value and cannot be used.';
  }
  return undefined;
}
//#endif
