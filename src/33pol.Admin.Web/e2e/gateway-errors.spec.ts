import { expect, test } from '@playwright/test';
import { openPrimaryTab, signInThroughGate } from './fixtures/gateway-auth';

test.beforeEach(async ({ page }) => {
  await signInThroughGate(page);
  await openPrimaryTab(page, 'Errors', '#panel-errors');
});

test('renders errors table headers', async ({ page }) => {
  await expect(page.locator('.t-errors, .data-table').first()).toBeVisible();
  await expect(page.getByText(/last seen|message|severity/i).first()).toBeVisible();
});
