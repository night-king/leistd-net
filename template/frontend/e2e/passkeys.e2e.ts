import { randomBytes } from 'node:crypto';

import { type Page, expect, test } from '@playwright/test';

import { admin, signIn } from './support/accounts';
import { json } from './support/http';

// 通行密钥走真实浏览器的 WebAuthn：Chromium 的虚拟认证器代替指纹或屏幕锁（自动通过用户验证），
// 服务端给的选项、浏览器交回的凭据与服务端校验在这里首尾相接，单元测试与接口测试都覆盖不到这一段。
test(
  'adds a passkey, then signs in with it without a password and as the second step',
  { tag: '@auth' },
  async ({ page }) => {
    const username = `pk_${randomBytes(4).toString('hex')}`;
    const password = `Pk!${randomBytes(12).toString('hex')}Aa1`;
    await signIn(page.request, admin());
    await json(
      await page.request.post('/api/v1/users', {
        data: {
          username,
          email: `${username}@example.com`,
          password,
          isActive: true,
          isEmailVerified: true,
        },
      }),
    );
    await page.context().clearCookies();
    await signIn(page.request, { key: 'admin', username, password });
    await addVirtualAuthenticator(page);

    // 刚登录的会话不必确认身份：点一次即调起浏览器创建，第一把带来恢复码
    await page.goto('/workspace/settings/security');
    await page.getByRole('button', { name: 'Add passkey' }).click();
    await page.getByTestId('recovery-codes-acknowledged').click();
    await page.getByTestId('recovery-codes-done').click();
    await expect
      .poll(
        async () =>
          (
            await json<{ passkeys: unknown[] }>(
              await page.request.get('/api/v1/auth/me/two-factor'),
            )
          ).passkeys.length,
      )
      .toBe(1);

    // 免口令登录：不填用户名，认证器里的可发现凭据认出账号。
    // 进入登录页即挂起条件式请求（用户名框的自动填充），虚拟认证器自动应答，不点任何东西就登录了
    await page.context().clearCookies();
    await page.goto('/auth/login');
    await expect(page).not.toHaveURL(/\/auth\//);
    expect(
      (await json<{ username: string }>(await page.request.get('/api/v1/auth/me'))).username,
    ).toBe(username);

    // 浏览器不支持条件式界面时只有按钮这一条路
    await page.addInitScript(() => {
      PublicKeyCredential.isConditionalMediationAvailable = () => Promise.resolve(false);
    });
    await page.context().clearCookies();
    await page.goto('/auth/login');
    await page.getByRole('button', { name: 'Sign in with a passkey' }).click();
    await expect(page).not.toHaveURL(/\/auth\//);

    // 口令登录后第二步默认就是通行密钥
    await page.context().clearCookies();
    await page.goto('/auth/login');
    await page.getByLabel('Username or Email').fill(username);
    await page.getByLabel('Password', { exact: true }).fill(password);
    await page.getByRole('button', { name: 'Sign in', exact: true }).click();
    await page.getByRole('button', { name: 'Use passkey' }).click();
    await expect(page).not.toHaveURL(/\/auth\//);
  },
);

async function addVirtualAuthenticator(page: Page): Promise<void> {
  const cdp = await page.context().newCDPSession(page);
  await cdp.send('WebAuthn.enable');
  await cdp.send('WebAuthn.addVirtualAuthenticator', {
    options: {
      protocol: 'ctap2',
      transport: 'internal',
      hasResidentKey: true,
      hasUserVerification: true,
      isUserVerified: true,
      automaticPresenceSimulation: true,
    },
  });
}
