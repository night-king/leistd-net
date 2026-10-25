/**
 * 浏览器 WebAuthn 调用失败的归类，界面按它给出本地化的说明，不展示浏览器原文（面向开发者，语言也不随界面）。
 *
 * - `cancelled`：本人关掉了系统对话框、没在时限内操作，或请求被中止。这是正常选择，只给中性提示。
 * - `alreadyRegistered`：这个认证器里已有本账号的通行密钥（登记时命中排除列表）。
 * - `failed`：其余一切（安全错误、认证器故障等）。
 */
export type WebAuthnFailure = 'cancelled' | 'alreadyRegistered' | 'failed';

export function classifyWebAuthnError(error: unknown): WebAuthnFailure {
  const name =
    typeof error === 'object' && error !== null ? (error as { name?: unknown }).name : undefined;
  switch (name) {
    case 'NotAllowedError':
    case 'AbortError':
      return 'cancelled';
    case 'InvalidStateError':
      return 'alreadyRegistered';
    default:
      return 'failed';
  }
}

/**
 * 是浏览器这一侧的失败（{@link classifyWebAuthnError} 能归类的 `DOMException`），而不是服务端的响应：
 * 调用方据此决定显示归类文案，还是服务端已本地化的原因。
 */
export function isWebAuthnError(error: unknown): boolean {
  return typeof DOMException !== 'undefined' && error instanceof DOMException;
}
