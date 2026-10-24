import { describePasskeyDevice } from './passkey-device-name';
import {
  createPasskey,
  fromBase64Url,
  getPasskey,
  isConditionalMediationAvailable,
  passkeySupport,
  toBase64Url,
} from './webauthn';
import { classifyWebAuthnError, isWebAuthnError } from './webauthn-error';
import {
  CREATION_OPTIONS,
  fakeAssertion,
  fakeAttestation,
  REQUEST_OPTIONS,
  webAuthnError,
} from './webauthn.testing';

describe('webauthn', () => {
  afterEach(() => vi.restoreAllMocks());

  it('round-trips base64url without padding', () => {
    const value = new Uint8Array([251, 255, 0, 1, 2]);

    const encoded = toBase64Url(value);

    expect(encoded).toBe('-_8AAQI');
    expect(Array.from(fromBase64Url(encoded))).toEqual(Array.from(value));
  });

  it('decodes the creation options and returns a credential the server can verify', async () => {
    const create = vi
      .spyOn(navigator.credentials, 'create')
      .mockResolvedValue(fakeAttestation('cred-1'));

    const credential = await createPasskey(CREATION_OPTIONS);

    const publicKey = create.mock.calls[0]![0]!.publicKey!;
    expect(Array.from(new Uint8Array(publicKey.challenge as ArrayBuffer))).toEqual([1, 2, 3]);
    expect(new TextDecoder().decode(publicKey.user.id as BufferSource)).toBe('user');
    expect(Array.from(publicKey.excludeCredentials![0]!.id as Uint8Array)).toEqual([4, 5, 6]);
    expect(credential).toEqual({
      id: 'cred-1',
      rawId: toBase64Url(new TextEncoder().encode('cred-1')),
      type: 'public-key',
      authenticatorAttachment: 'platform',
      response: {
        clientDataJSON: toBase64Url(new TextEncoder().encode('{"type":"webauthn.create"}')),
        attestationObject: toBase64Url(new TextEncoder().encode('attestation-object')),
        transports: ['internal', 'hybrid'],
      },
      clientExtensionResults: {},
    });
  });

  it('decodes the request options and returns the assertion', async () => {
    const get = vi.spyOn(navigator.credentials, 'get').mockResolvedValue(fakeAssertion('cred-2'));

    const assertion = await getPasskey(REQUEST_OPTIONS);

    const request = get.mock.calls[0]![0]!;
    expect(request.mediation).toBeUndefined();
    expect(Array.from(request.publicKey!.allowCredentials![0]!.id as Uint8Array)).toEqual([
      4, 5, 6,
    ]);
    expect(assertion).toMatchObject({
      id: 'cred-2',
      type: 'public-key',
      response: {
        signature: toBase64Url(new TextEncoder().encode('signature')),
        userHandle: toBase64Url(new TextEncoder().encode('user-handle')),
      },
      clientExtensionResults: {},
    });
  });

  it('passes conditional mediation and the abort signal through', async () => {
    const get = vi.spyOn(navigator.credentials, 'get').mockResolvedValue(fakeAssertion());
    const controller = new AbortController();

    await getPasskey(REQUEST_OPTIONS, { mediation: 'conditional', signal: controller.signal });

    expect(get.mock.calls[0]![0]).toMatchObject({
      mediation: 'conditional',
      signal: controller.signal,
    });
  });

  it('treats a missing credential as a cancellation', async () => {
    vi.spyOn(navigator.credentials, 'get').mockResolvedValue(null);

    const failure = await getPasskey(REQUEST_OPTIONS).catch((error: unknown) => error);

    expect(classifyWebAuthnError(failure)).toBe('cancelled');
  });

  it('classifies browser failures without exposing the browser text', () => {
    expect(classifyWebAuthnError(webAuthnError('NotAllowedError'))).toBe('cancelled');
    expect(classifyWebAuthnError(webAuthnError('AbortError'))).toBe('cancelled');
    expect(classifyWebAuthnError(webAuthnError('InvalidStateError'))).toBe('alreadyRegistered');
    expect(classifyWebAuthnError(webAuthnError('SecurityError'))).toBe('failed');
    expect(classifyWebAuthnError(new Error('boom'))).toBe('failed');
    expect(isWebAuthnError(webAuthnError())).toBe(true);
    expect(isWebAuthnError(new Error('boom'))).toBe(false);
  });

  it('reports support in a secure context and none without WebAuthn', () => {
    expect(passkeySupport()).toBe('supported');

    vi.stubGlobal('PublicKeyCredential', undefined);
    try {
      expect(passkeySupport()).toBe('unsupported');
    } finally {
      vi.unstubAllGlobals();
    }
  });

  it('reads conditional mediation availability and treats a failing probe as unavailable', async () => {
    const probe = vi
      .spyOn(PublicKeyCredential, 'isConditionalMediationAvailable')
      .mockResolvedValue(true);
    expect(await isConditionalMediationAvailable()).toBe(true);

    probe.mockRejectedValue(new Error('probe failed'));
    expect(await isConditionalMediationAvailable()).toBe(false);
  });

  describe('describePasskeyDevice', () => {
    it('prefers user agent client hints and skips placeholder brands', () => {
      expect(
        describePasskeyDevice(
          {
            platform: 'macOS',
            brands: [{ brand: 'Not.A/Brand' }, { brand: 'Chromium' }, { brand: 'Google Chrome' }],
          },
          'ignored',
        ),
      ).toBe('macOS · Chrome');
    });

    it('falls back to the user agent without a version number', () => {
      expect(
        describePasskeyDevice(
          null,
          'Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1',
        ),
      ).toBe('iOS · Safari');
    });

    it('returns null when nothing is recognised', () => {
      expect(describePasskeyDevice({ platform: 'Unknown', brands: [] }, 'curl/8')).toBeNull();
    });
  });
});
