/** 与后端约定的租户传递键，全前端只在这里定义；取值是后端默认值，后端改名时同步改这里。 */

/**
 * 匿名请求的租户提示头：登录、注册等尚无身份的请求靠它指明租户，值可以是租户名或 id。
 * 后端对应 `MultiTenancyOptions.HeaderName`。已登录请求的租户由服务端身份定案，提示头不起作用。
 */
export const TENANT_HEADER = 'X-Tenant';

/** 租户会话失效标记：服务端注销了租户已不可用的会话时带上它。后端对应 `TenantSessionRecoveryOptions.TenantInvalidHeader`。 */
export const TENANT_INVALID_HEADER = 'X-Tenant-Invalid';
