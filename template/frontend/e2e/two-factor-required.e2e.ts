import { randomBytes } from 'node:crypto';

import { type APIRequestContext, expect, request, test } from '@playwright/test';

import { admin, signIn } from './support/accounts';
import { json } from './support/http';
import { freshTotp } from './support/totp';

// 组织（租户）要求两步验证后，还没开启的账号登录只能先在页面上完成设置。
// 要求放在本用例新建的租户上：只限制这个租户的会话，不影响并行用例用的宿主账号。
test(
  'completes the required two-factor setup on the page and signs in',
  { tag: '@auth' },
  async ({ page, baseURL }) => {
    const tenant = `mfa-${randomBytes(4).toString('hex')}`;
    const tenantAdminPassword = `Tnt!${randomBytes(12).toString('hex')}Aa1`;
    const username = `mfa_${randomBytes(4).toString('hex')}`;
    const password = `Mfa!${randomBytes(12).toString('hex')}Aa1`;

    const host = await request.newContext({ baseURL });
    const organization = await request.newContext({
      baseURL,
      extraHTTPHeaders: { 'X-Tenant': tenant },
    });
    try {
      await signIn(host, admin());
      await json(
        await host.post('/api/v1/tenants', {
          data: {
            name: tenant,
            displayName: tenant,
            adminEmail: `admin@${tenant}.example.com`,
            adminPassword: tenantAdminPassword,
          },
        }),
      );
      await signIn(organization, {
        key: 'admin',
        username: 'admin',
        password: tenantAdminPassword,
      });
      await json(
        await organization.post('/api/v1/users', {
          data: { username, email: `${username}@example.com`, password, isEmailVerified: true },
        }),
      );
      await requireTwoFactor(organization);
    } finally {
      await host.dispose();
      await organization.dispose();
    }

    await page.goto('/auth/login');
    await page.getByTestId('tenant-switch').click();
    await page.locator('#tenantName').fill(tenant);
    await page.getByRole('button', { name: 'Confirm', exact: true }).click();
    await expect(page.getByTestId('tenant-row')).toContainText(tenant);
    await page.getByLabel('Username or Email').fill(username);
    await page.getByLabel('Password', { exact: true }).fill(password);
    await page.getByRole('button', { name: 'Sign in' }).click();

    // 受限会话只能去设置页；密钥取自页面，验证码按 RFC 6238 现算
    await expect(page).toHaveURL(/\/auth\/two-factor-setup/);
    const secret = await page.getByTestId('two-factor-secret').textContent();
    await page.locator('#two-factor-setup-code').fill(await freshTotp(secret ?? ''));
    await page.getByTestId('two-factor-enable').click();
    await expect(page.getByTestId('recovery-codes').locator('li').first()).toBeVisible();
    await page.locator('label[for="recovery-codes-acknowledged"]').click();
    await page.getByTestId('recovery-codes-done').click();

    // 同一会话解除限制：离开认证页，受保护接口放行
    await expect(page).not.toHaveURL(/\/auth\//);
    const me = await json<{ isTwoFactorEnabled: boolean }>(
      await page.request.get('/api/v1/auth/me'),
    );
    expect(me.isTwoFactorEnabled).toBe(true);
  },
);

async function requireTwoFactor(context: APIRequestContext): Promise<void> {
  await json(
    await context.put('/api/v1/settings/current-tenant', {
      data: { name: 'Security.RequireTwoFactor', value: 'true' },
    }),
  );
}
