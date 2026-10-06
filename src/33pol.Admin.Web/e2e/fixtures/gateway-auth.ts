import type { Page } from '@playwright/test';

/** Matches GatewayWebApplicationFactory bootstrap admin key. */
export const ADMIN_E2E_KEY = process.env.ADMIN_E2E_KEY ?? 'sk-33pol-integration-admin-key';

export async function signInThroughGate(page: Page, apiKey = ADMIN_E2E_KEY): Promise<void> {
  await page.goto('./');
  await page.getByLabel('Admin API key').fill(apiKey);
  await page.getByRole('button', { name: 'Connect' }).click();
  await page.getByRole('navigation', { name: 'Main' }).waitFor();
}

export async function openPrimaryTab(page: Page, label: string, panel: string): Promise<void> {
  await page.getByRole('tab', { name: label }).click();
  await page.locator(panel).waitFor({ state: 'visible' });
}
