/**
 * WebAuthn 的测试装配：真实 Chromium 里没有认证器，`navigator.credentials` 一律打桩，
 * 交回形状与浏览器一致的假凭据（二进制字段是 `ArrayBuffer`）。
 */

function bytes(text: string): ArrayBuffer {
  return new TextEncoder().encode(text).buffer as ArrayBuffer;
}

/** 浏览器 `get()` 交回的断言。 */
export function fakeAssertion(id = 'credential-1'): PublicKeyCredential {
  return {
    id,
    rawId: bytes(id),
    type: 'public-key',
    authenticatorAttachment: 'platform',
    response: {
      clientDataJSON: bytes('{"type":"webauthn.get"}'),
      authenticatorData: bytes('authenticator-data'),
      signature: bytes('signature'),
      userHandle: bytes('user-handle'),
    },
    getClientExtensionResults: () => ({}),
  } as unknown as PublicKeyCredential;
}

/** 浏览器 `create()` 交回的凭据。 */
export function fakeAttestation(id = 'credential-1'): PublicKeyCredential {
  return {
    id,
    rawId: bytes(id),
    type: 'public-key',
    authenticatorAttachment: 'platform',
    response: {
      clientDataJSON: bytes('{"type":"webauthn.create"}'),
      attestationObject: bytes('attestation-object'),
      getTransports: () => ['internal', 'hybrid'],
    },
    getClientExtensionResults: () => ({}),
  } as unknown as PublicKeyCredential;
}

/** 浏览器这一侧的失败（本人取消为 `NotAllowedError`）。 */
export function webAuthnError(name = 'NotAllowedError'): DOMException {
  return new DOMException('browser text that must not be shown', name);
}

/** 服务端给的断言选项（二进制字段是 base64url）。 */
export const REQUEST_OPTIONS = {
  challenge: 'AQID',
  timeout: 300000,
  userVerification: 'required',
  allowCredentials: [{ type: 'public-key', id: 'BAUG' }],
};

/** 服务端给的创建选项。 */
export const CREATION_OPTIONS = {
  rp: { name: 'Test' },
  user: { id: 'dXNlcg', name: 'admin', displayName: 'Admin' },
  challenge: 'AQID',
  pubKeyCredParams: [{ type: 'public-key', alg: -7 }],
  excludeCredentials: [{ type: 'public-key', id: 'BAUG' }],
};
