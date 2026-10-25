import { WebAuthnJson } from '../../shared/dtos/account-security.dto';

/**
 * 浏览器 WebAuthn（`navigator.credentials`）的薄封装：输入服务端给的选项 JSON，输出可以原样提交给服务端的凭据 JSON。
 * 两边的二进制字段（挑战、用户句柄、凭据 Id、签名等）都是 base64url 字符串，这里与浏览器要的 `ArrayBuffer` 互转。
 *
 * 不依赖浏览器较新的 `PublicKeyCredential.parseCreationOptionsFromJSON()` / `toJSON()`：尚未覆盖全部主流版本，
 * 而手写的互转只有几十行、各浏览器结果一致。浏览器抛出的异常原样抛给调用方，由 {@link classifyWebAuthnError} 归类。
 */

/** 浏览器此刻能不能用通行密钥：不支持 WebAuthn，或页面不在安全上下文（HTTPS 或 localhost）里时不能。 */
export type PasskeySupport = 'supported' | 'unsupported' | 'insecureContext';

export function passkeySupport(): PasskeySupport {
  if (typeof window === 'undefined' || typeof window.PublicKeyCredential !== 'function') {
    return 'unsupported';
  }
  return window.isSecureContext ? 'supported' : 'insecureContext';
}

/**
 * 浏览器支持条件式界面（用户名框的自动填充里列出通行密钥）。不支持时登录页只给按钮；检测本身出错也按不支持。
 */
export async function isConditionalMediationAvailable(): Promise<boolean> {
  if (passkeySupport() !== 'supported') {
    return false;
  }
  try {
    return (await PublicKeyCredential.isConditionalMediationAvailable?.()) === true;
  } catch {
    return false;
  }
}

/** 断言的发起方式。 */
export interface PasskeyRequestOptions {
  /** `conditional`：不弹窗，等本人在自动填充里选中；只用于登录页的用户名框。 */
  readonly mediation?: 'conditional';
  /** 中止进行中的请求（离开页面、改用按钮发起）；中止后以 `AbortError` 拒绝。 */
  readonly signal?: AbortSignal;
}

/** 登记：按创建选项调起浏览器创建通行密钥，返回交给服务端校验的凭据。 */
export async function createPasskey(options: WebAuthnJson): Promise<WebAuthnJson> {
  const user = options['user'] as WebAuthnJson;
  const publicKey = {
    ...options,
    challenge: fromBase64Url(options['challenge'] as string),
    user: { ...user, id: fromBase64Url(user['id'] as string) },
    excludeCredentials: descriptors(options['excludeCredentials']),
  } as unknown as PublicKeyCredentialCreationOptions;

  const credential = (await navigator.credentials.create({
    publicKey,
  })) as PublicKeyCredential | null;
  // 没有凭据也没有异常：按本人取消处理，与浏览器取消时抛的异常同名
  if (!credential) {
    throw new DOMException('The passkey was not created.', 'NotAllowedError');
  }

  const response = credential.response as AuthenticatorAttestationResponse;
  return {
    id: credential.id,
    rawId: toBase64Url(credential.rawId),
    type: credential.type,
    authenticatorAttachment: credential.authenticatorAttachment ?? null,
    response: {
      clientDataJSON: toBase64Url(response.clientDataJSON),
      attestationObject: toBase64Url(response.attestationObject),
      transports: response.getTransports?.() ?? [],
    },
    clientExtensionResults: {},
  };
}

/** 验证：按断言选项调起浏览器，返回交给服务端校验的断言。 */
export async function getPasskey(
  options: WebAuthnJson,
  request: PasskeyRequestOptions = {},
): Promise<WebAuthnJson> {
  const publicKey = {
    ...options,
    challenge: fromBase64Url(options['challenge'] as string),
    allowCredentials: descriptors(options['allowCredentials']),
  } as unknown as PublicKeyCredentialRequestOptions;

  const credential = (await navigator.credentials.get({
    publicKey,
    ...(request.mediation ? { mediation: request.mediation } : {}),
    ...(request.signal ? { signal: request.signal } : {}),
  })) as PublicKeyCredential | null;
  if (!credential) {
    throw new DOMException('The passkey was not used.', 'NotAllowedError');
  }

  const response = credential.response as AuthenticatorAssertionResponse;
  return {
    id: credential.id,
    rawId: toBase64Url(credential.rawId),
    type: credential.type,
    authenticatorAttachment: credential.authenticatorAttachment ?? null,
    response: {
      clientDataJSON: toBase64Url(response.clientDataJSON),
      authenticatorData: toBase64Url(response.authenticatorData),
      signature: toBase64Url(response.signature),
      userHandle: response.userHandle ? toBase64Url(response.userHandle) : null,
    },
    clientExtensionResults: {},
  };
}

/** base64url（无填充）→ 字节；服务端选项里的二进制字段都是这种写法。 */
export function fromBase64Url(value: string): Uint8Array<ArrayBuffer> {
  const base64 = value.replace(/-/g, '+').replace(/_/g, '/');
  const padded = base64.padEnd(Math.ceil(base64.length / 4) * 4, '=');
  return Uint8Array.from(atob(padded), (character) => character.charCodeAt(0));
}

/** 字节 → base64url（无填充）。 */
export function toBase64Url(value: ArrayBuffer | ArrayBufferView): string {
  const bytes =
    value instanceof ArrayBuffer
      ? new Uint8Array(value)
      : new Uint8Array(value.buffer, value.byteOffset, value.byteLength);
  let binary = '';
  for (const byte of bytes) {
    binary += String.fromCharCode(byte);
  }
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

function descriptors(value: unknown): PublicKeyCredentialDescriptor[] {
  return ((value as WebAuthnJson[] | undefined) ?? []).map(
    (item) =>
      ({
        ...item,
        id: fromBase64Url(item['id'] as string),
      }) as unknown as PublicKeyCredentialDescriptor,
  );
}
