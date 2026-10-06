import { expect, test } from '@playwright/test';
import { installAdminApiMocks, openPrimaryTab, signInThroughGate } from './fixtures/admin-api';

test.beforeEach(async ({ page }) => {
  await installAdminApiMocks(page);
  await signInThroughGate(page);
  await openPrimaryTab(page, 'Keys', '#panel-keys');
});

test('creates a key and shows the one-time secret', async ({ page }) => {
  await page.getByRole('button', { name: 'Create key' }).click();
  await expect(page.getByRole('heading', { name: 'Create API key' })).toBeVisible();

  await page.getByPlaceholder('e.g. prod-chatbot').fill('e2e-bot');
  await page.getByPlaceholder('Person or team name').fill('qa');
  await page.getByRole('dialog').getByRole('button', { name: 'Create key' }).click();

  await expect(page.getByText(/copy now/i)).toBeVisible();
  await expect(page.getByText('sk-new-secret-shown-once')).toBeVisible();

  await page.getByLabel(/saved this secret/i).check();
  await page.keyboard.press('Escape');
  await expect(page.getByRole('heading', { name: 'Create API key' })).toHaveCount(0);
});

test('offers archived and revoked status filters', async ({ page }) => {
  const statusSelect = page.locator('#panel-keys .filter-row select').first();
  await expect(statusSelect.locator('option[value="archived"]')).toHaveText('Archived');
  await expect(statusSelect.locator('option[value="revoked"]')).toHaveText('Revoked');
  await statusSelect.selectOption('all');
  await expect(page.getByText('sk-live')).toBeVisible();
});
