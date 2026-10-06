import { expect, test } from '@playwright/test';
import { openPrimaryTab, signInThroughGate } from './fixtures/gateway-auth';

test.beforeEach(async ({ page }) => {
  await signInThroughGate(page);
  await openPrimaryTab(page, 'Settings', '#panel-settings');
  await page.getByRole('tab', { name: 'CORS' }).click();
});

test('loads, edits, and saves CORS origins', async ({ page }) => {
  await expect(page.getByText('CORS allowed origins')).toBeVisible();
  const originInput = page.locator('#panel-settings input[placeholder="https://example.com"]').first();
  await originInput.fill('https://e2e.test');
  await page.getByRole('button', { name: 'Save CORS' }).click();
  await expect(page.locator('.toast.success')).toContainText(/CORS.*updated/i, { timeout: 20_000 });
});
