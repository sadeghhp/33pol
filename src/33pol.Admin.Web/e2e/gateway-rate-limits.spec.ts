import { expect, test } from '@playwright/test';
import { openPrimaryTab, signInThroughGate } from './fixtures/gateway-auth';

test.beforeEach(async ({ page }) => {
  await signInThroughGate(page);
  await openPrimaryTab(page, 'Settings', '#panel-settings');
  await page.getByRole('tab', { name: 'Rate limits' }).click();
});

test('loads rate limits panel and saves default tier change', async ({ page }) => {
  await expect(page.locator('.rate-limits-panel')).toBeVisible({ timeout: 20_000 });
  const firstRule = page.locator('.rate-limits-panel .rl-rules-table tbody tr').first();
  await expect(firstRule).toBeVisible();
  await firstRule.locator('.rl-col-on label.rl-switch').click();
  await expect(page.locator('.rate-limits-panel .rl-savebar')).toBeVisible();
  await page.locator('.rate-limits-panel .rl-savebar').getByRole('button', { name: 'Save' }).click();
  await expect(page.locator('.toast.success')).toContainText(/saved/i, { timeout: 20_000 });
});
