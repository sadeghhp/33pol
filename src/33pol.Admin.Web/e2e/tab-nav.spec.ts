import { expect, test } from '@playwright/test';
import { installAdminApiMocks, openPrimaryTab, signInThroughGate } from './fixtures/admin-api';

const TABS = [
  { label: 'Overview', panel: '#panel-dashboard', heading: 'Overview' },
  { label: 'Usage', panel: '#panel-usage', heading: 'Usage' },
  { label: 'Routing', panel: '#panel-routing', heading: 'Routing' },
  { label: 'Keys', panel: '#panel-keys', heading: 'API keys' },
  { label: 'Logs', panel: '#panel-logs', heading: 'Logs' },
  { label: 'Errors', panel: '#panel-errors', heading: 'Errors' },
  { label: 'Settings', panel: '#panel-settings', heading: 'Settings' },
] as const;

test.beforeEach(async ({ page }) => {
  await installAdminApiMocks(page);
  await signInThroughGate(page);
});

test('navigates across all primary tabs', async ({ page }) => {
  for (const tab of TABS) {
    await openPrimaryTab(page, tab.label, tab.panel);
    await expect(page.getByRole('heading', { name: tab.heading, level: 1 })).toBeVisible();
  }
});

test('preserves legacy hash redirects for routing sub-tabs', async ({ page }) => {
  await page.goto('./#/models');
  await expect(page.locator('#panel-routing')).toBeVisible();
  await expect(page.getByRole('tab', { name: 'Models' })).toHaveAttribute('aria-selected', 'true');

  await page.goto('./#/backends');
  await expect(page.locator('#panel-routing')).toBeVisible();
  await expect(page.getByRole('tab', { name: 'Backends' })).toHaveAttribute('aria-selected', 'true');
});
