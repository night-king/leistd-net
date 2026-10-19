import { expect, test } from '@playwright/test';

import { admin } from './support/accounts';

// 唯一走登录界面的用例；其余用例复用全局准备经接口登录保存的会话
test('signs in through the login page', async ({ page }) => {
  const account = admin();
  await page.goto('/auth/login');
  await page.getByLabel('Username or Email').fill(account.username);
  await page.getByLabel('Password', { exact: true }).fill(account.password);
  await page.getByRole('button', { name: 'Sign In' }).click();

  await expect(page).not.toHaveURL(/\/auth\/login/);
  await page.goto('/platform/roles');
  await expect(page.getByRole('button', { name: 'New role' })).toBeVisible();
});
