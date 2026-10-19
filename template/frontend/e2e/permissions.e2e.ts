import { expect, test } from '@playwright/test';

import { admin, authStatePath, restricted } from './support/accounts';

// 同一页面按权限显示操作：受限角色只有查看权限，看得到列表、看不到新建入口
test.describe('administrator', { tag: '@auth' }, () => {
  test.use({ storageState: authStatePath(admin()) });

  test('can create roles', async ({ page }) => {
    await page.goto('/platform/roles');
    await expect(page.getByRole('row').filter({ hasText: 'E2E viewer' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'New role' })).toBeVisible();
  });
});

test.describe('restricted user', { tag: '@auth' }, () => {
  test.use({ storageState: authStatePath(restricted()) });

  test('sees roles without the create action', async ({ page }) => {
    await page.goto('/platform/roles');
    // 先等列表数据到达，再断言入口不存在，否则页面未渲染完也会通过
    await expect(page.getByRole('row').filter({ hasText: 'E2E viewer' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'New role' })).toHaveCount(0);
  });
});
