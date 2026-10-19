import { randomBytes } from 'node:crypto';

import { expect, test } from '@playwright/test';

import { admin, signIn } from './support/accounts';
import { json } from './support/http';

// 改邮箱这类操作要求会话"刚认证过"（15 分钟内），否则当场确认身份。
// 不等真实时间、也不改服务端时间：模拟登录签发的会话一律不算刚认证，用它走到同一个确认对话框。
test(
  'confirms identity with the password before changing the email',
  { tag: '@auth' },
  async ({ page }) => {
    const tenant = `e2e-${randomBytes(4).toString('hex')}`;
    const tenantAdminPassword = `Tnt!${randomBytes(12).toString('hex')}Aa1`;
    // 本用例自己的管理员会话：模拟登录会换掉会话，不能动全局准备保存的那一份
    await signIn(page.request, admin());
    const created = await json<{ id: string }>(
      await page.request.post('/api/v1/tenants', {
        data: {
          name: tenant,
          displayName: tenant,
          adminEmail: `admin@${tenant}.example.com`,
          adminPassword: tenantAdminPassword,
        },
      }),
    );
    await json(await page.request.post(`/api/v1/tenants/${created.id}/impersonate`));

    const changed = `changed@${tenant}.example.com`;
    await page.goto('/workspace/settings/profile');
    await page.locator('#profile-email').fill(changed);
    await page.locator('app-profile-panel form button[type="submit"]').click();

    const dialog = page.getByTestId('reauthenticate-form');
    await expect(dialog).toHaveAttribute('data-method', 'password');
    await dialog.locator('input[type="password"]').fill(tenantAdminPassword);
    await page.getByTestId('reauthenticate-submit').click();

    // 确认后重试原来那次保存：对话框关闭、仍在原页面，邮箱已写入
    await expect(dialog).toHaveCount(0);
    await expect(page).toHaveURL(/\/workspace\/settings\/profile$/);
    await expect
      .poll(
        async () =>
          (await json<{ email: string }>(await page.request.get('/api/v1/auth/me'))).email,
      )
      .toBe(changed);
  },
);
