//#if (RemoteTokenAuth)
import { RESOURCE_AUTH_API } from './resource-auth';
import { MockException } from '../core/models';
import { USERS } from '../data/user';
import {
  getMockSessionSubjectId,
  getMockSessionTenantKey,
  setMockSessionIdentity,
  setMockSessionUserId,
} from '../utils/current-user';

describe('Resource mock server session', () => {
  beforeEach(() => setMockSessionUserId(null));
  afterEach(() => setMockSessionUserId(null));

  it('requires a session and removes it on logout', () => {
    expect(() => RESOURCE_AUTH_API['GET /api/v1/auth/me']()).toThrow(MockException);
    RESOURCE_AUTH_API['POST /api/v1/auth/login']();
    expect(RESOURCE_AUTH_API['GET /api/v1/auth/me']().id).toBe(USERS[0].id);
    RESOURCE_AUTH_API['POST /api/v1/auth/logout']();
    expect(() => RESOURCE_AUTH_API['GET /api/v1/auth/me']()).toThrow(MockException);
  });

  //#if (IncludeMultiTenancy)
  it('keeps the server subject distinct from its display persona and pins the tenant', () => {
    const subject = crypto.randomUUID();
    const tenant = crypto.randomUUID();
    setMockSessionIdentity(subject, USERS[0].id, tenant);
    expect(getMockSessionSubjectId()).toBe(subject);
    expect(getMockSessionTenantKey()).toBe(tenant);
    expect(RESOURCE_AUTH_API['GET /api/v1/auth/me']().tenantId).toBe(tenant);
  });
  //#else
  it('rejects a tenant identity before publishing a subject and accepts a host identity', () => {
    const subject = crypto.randomUUID();
    expect(() => setMockSessionIdentity(subject, USERS[0].id, crypto.randomUUID())).toThrow(
      MockException,
    );
    expect(getMockSessionSubjectId()).toBeNull();
    expect(() => RESOURCE_AUTH_API['GET /api/v1/auth/me']()).toThrow(MockException);
    setMockSessionIdentity(subject, USERS[0].id, null);
    expect(getMockSessionSubjectId()).toBe(subject);
    expect(getMockSessionTenantKey()).toBe('host');
    expect(RESOURCE_AUTH_API['GET /api/v1/auth/me']().tenantId).toBeNull();
  });

  it('rejects a previously stored tenant session before returning a user', () => {
    setMockSessionIdentity(crypto.randomUUID(), USERS[0].id, null);
    sessionStorage.setItem('mock_session_tenant_key', crypto.randomUUID());
    expect(() => RESOURCE_AUTH_API['GET /api/v1/auth/me']()).toThrow(MockException);
  });
  //#endif
});
//#endif
