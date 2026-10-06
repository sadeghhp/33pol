import { expect, test } from '@playwright/test';
import { signInThroughGate } from './fixtures/gateway-auth';

const TABS = [
  { label: 'Overview', panel: '#panel-dashboard' },
  { label: 'Keys', panel: '#panel-keys' },
  { label: 'Routing', panel: '#panel-routing' },
  { label: 'Usage', panel: '#panel-usage' },
  { label: 'Errors', panel: '#panel-errors' },
  { label: 'Logs', panel: '#panel-logs' },
  { label: 'Settings', panel: '#panel-settings' },
];

test.beforeEach(async ({ page }) => {
  await signInThroughGate(page);
});

test('navigates across all primary tabs', async ({ page }) => {
  for (const tab of TABS) {
    await page.getByRole('tab', { name: tab.label }).click();
    await expect(page.locator(tab.panel)).toBeVisible();
  }
});

test('preserves legacy hash redirects for routing sub-tabs', async ({ page }) => {
  await page.goto('./#/models');
  await expect(page.locator('#panel-routing')).toBeVisible();
  await page.goto('./#/backends');
  await expect(page.locator('#panel-routing')).toBeVisible();
});
