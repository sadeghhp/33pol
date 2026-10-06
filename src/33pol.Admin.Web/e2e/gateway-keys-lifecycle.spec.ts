import { expect, test } from '@playwright/test';
import { openPrimaryTab, signInThroughGate } from './fixtures/gateway-auth';

test.beforeEach(async ({ page }) => {
  await signInThroughGate(page);
  await openPrimaryTab(page, 'Keys', '#panel-keys');
});

test('creates edits and revokes a key', async ({ page }) => {
  const label = `e2e-${Date.now()}`;

  await page.getByRole('button', { name: 'Create key' }).click();
  await page.getByPlaceholder('e.g. prod-chatbot').fill(label);
  await page.getByRole('dialog').getByRole('button', { name: 'Create key' }).click();
  await expect(page.getByText(/copy now/i)).toBeVisible();
  await page.getByLabel(/saved this secret/i).check();
  await page.keyboard.press('Escape');

  await page.getByRole('button', { name: 'Refresh' }).click();
  const row = page.locator('#panel-keys tbody tr', { hasText: label });
  await expect(row).toBeVisible({ timeout: 20_000 });

  await row.getByRole('button', { name: 'Edit' }).click();
  const editDrawer = page.locator('.drawer[role="dialog"]');
  await expect(editDrawer.getByRole('heading', { name: 'Edit API key' })).toBeVisible();
  await editDrawer.getByRole('textbox').first().fill(`${label}-edited`);
  await editDrawer.getByRole('button', { name: 'Save' }).click();
  await expect(page.locator('.toast.success')).toContainText(/updated/i);

  const editedRow = page.locator('#panel-keys tbody tr', { hasText: `${label}-edited` });
  await editedRow.getByRole('button', { name: 'Revoke' }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Revoke' }).click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await page.locator('#panel-keys .filter-row select').first().selectOption('revoked');
  const revokedRow = page.locator('#panel-keys tbody tr', { hasText: `${label}-edited` });
  await expect(revokedRow.getByText('revoked')).toBeVisible();
});
