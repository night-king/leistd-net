import { WORKSPACE_ROUTES } from './workspace.routes';

/**
 * 个人设置挂在 workspace，只要求认证：挂在 `/platform` 下时普通用户与只有 App.Settings 的管理员
 * 都进不去。
 */
describe('workspace routes', () => {
  it('exposes settings without any permission requirement', () => {
    const settings = WORKSPACE_ROUTES.find((route) => route.path === 'settings');

    expect(settings).toBeDefined();
    // workspace 的父路由只有 authGuard；这里再挂权限就把普通用户挡回去了
    expect(settings?.canActivate).toBeUndefined();
    expect(settings?.data?.['permission']).toBeUndefined();
  });
});
