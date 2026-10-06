import { expect, test } from '@playwright/test';
import { openPrimaryTab, signInThroughGate } from './fixtures/gateway-auth';

test.beforeEach(async ({ page }) => {
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
  await expect(page.locator('.secret-display')).toContainText(/^sk-/);

  await page.getByLabel(/saved this secret/i).check();
  await page.keyboard.press('Escape');
  await expect(page.getByRole('heading', { name: 'Create API key' })).toHaveCount(0);
});

test('offers archived and revoked status filters', async ({ page }) => {
  const statusSelect = page.locator('#panel-keys .filter-row select').first();
  await expect(statusSelect.locator('option[value="archived"]')).toHaveText('Archived');
  await expect(statusSelect.locator('option[value="revoked"]')).toHaveText('Revoked');
});
