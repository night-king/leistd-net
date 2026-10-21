//#if (RemoteTokenAuth)
import { MockException } from '../core/models';
import { USERS, toUserOutput } from '../data/user';
import {
  getCurrentUser,
  getMockSessionTenantKey,
  setMockSessionIdentity,
  setMockSessionUserId,
} from '../utils/current-user';

/** 模拟服务端 Cookie 会话；不生成或解析 OAuth 令牌。 */
export const RESOURCE_AUTH_API = {
  'POST /api/v1/auth/login': () => {
    setMockSessionIdentity(USERS[0].id, USERS[0].id, null);
    return {};
  },
  'GET /api/v1/auth/me': () => {
    const user = getCurrentUser();
    if (!user) throw new MockException(401, 'No active session');
    const tenantId = getMockSessionTenantKey();
    return { ...toUserOutput(user), tenantId: tenantId === 'host' ? null : tenantId };
  },
  'POST /api/v1/auth/logout': () => {
    setMockSessionUserId(null);
    return {};
  },
};
//#endif
