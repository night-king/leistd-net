// 刻意不 Mock 的端点：浏览器整页跳转或经 <img> 加载、不走 HttpClient 的入口。每项写成 `方法 路径`：
// - `GET /api/v1/users/{id}/avatar`：头像图片经 <img> 加载。
//#if (ExternalLogin)
// - `GET /api/v1/external-auth/{provider}/challenge`、`GET /api/v1/external-auth/{provider}/link/challenge`：外部登录与绑定的整页跳转。
//#endif
//#if (RemoteTokenAuth)
// - `GET /api/v1/auth/login`：302 到身份服务。
// 只在 Mock 中存在的端点：上面的整页跳转在 Mock 下的替身。
// - `POST /api/v1/auth/login`：模拟登录，直接建立 Mock 会话。
//#endif
//#if (LocalIdentity)
export * from './api/auth';
//#endif
export * from './api/user';
export * from './api/authorization';
export * from './api/setting';
export * from './api/platform-statistics';
//#if (IncludeOperationRecords)
export * from './api/operation-record';
//#endif
//#if (LocalIdentity && IncludeMultiTenancy)
export * from './api/tenant';
//#endif
//#if (OpenIddictServer)
export * from './api/open-application';
//#endif
//#if (IncludeNotifications)
export * from './api/notification';
//#endif
// 确保本文件在任意条件下都是一个有效模块（无认证模块时无 mock API 导出）。
export {};
//#if (RemoteTokenAuth)
export { RESOURCE_AUTH_API } from './api/resource-auth';
//#endif
