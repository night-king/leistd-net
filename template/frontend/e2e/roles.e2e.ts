import { expect, test } from '@playwright/test';

import { admin, authStatePath } from './support/accounts';

test.use({ storageState: authStatePath(admin()) });

test('creates, edits and deletes a role', { tag: '@crud' }, async ({ page }) => {
  const name = `e2e_${Date.now()}`;
  const displayName = `Role ${name}`;
  const renamed = `${displayName} renamed`;
  await page.goto('/platform/roles');

  await page.getByRole('button', { name: 'New role' }).click();
  const createDialog = page.getByRole('dialog');
  await createDialog.getByLabel('Name', { exact: true }).fill(name);
  await createDialog.getByLabel('Display name').fill(displayName);
  await createDialog.getByRole('button', { name: 'Save' }).click();
  await expect(page.getByRole('row').filter({ hasText: displayName })).toBeVisible();

  await page.getByRole('button', { name: `Edit - ${displayName}` }).click();
  const editDialog = page.getByRole('dialog');
  await editDialog.getByLabel('Display name').fill(renamed);
  await editDialog.getByRole('button', { name: 'Save' }).click();
  await expect(page.getByRole('row').filter({ hasText: renamed })).toBeVisible();

  await page.getByRole('button', { name: `Delete - ${renamed}` }).click();
  await page.getByRole('alertdialog').getByRole('button', { name: 'Delete' }).click();
  await expect(page.getByRole('row').filter({ hasText: renamed })).toHaveCount(0);
});
