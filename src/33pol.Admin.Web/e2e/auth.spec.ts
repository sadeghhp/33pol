import { expect, test } from '@playwright/test';
import { ADMIN_E2E_KEY, signInThroughGate } from './fixtures/gateway-auth';

test('shows sign-in gate before authentication', async ({ page }) => {
  await page.goto('./');
  await expect(page.getByRole('heading', { name: '33pol control plane' })).toBeVisible();
  await expect(page.getByLabel('Admin API key')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Connect' })).toBeVisible();
  await expect(page.getByRole('navigation', { name: 'Main' })).toHaveCount(0);
});

test('connects with an admin key and reveals the shell', async ({ page }) => {
  await signInThroughGate(page, ADMIN_E2E_KEY);
  await expect(page.locator('#panel-dashboard')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Sign out' })).toBeVisible();
});

test('rejects an invalid key with feedback', async ({ page }) => {
  await page.goto('./');
  await page.getByLabel('Admin API key').fill('sk-bad-key-not-valid');
  await page.getByRole('button', { name: 'Connect' }).click();
  await expect(page.getByText(/invalid or missing admin api key/i)).toBeVisible();
  await expect(page.getByRole('navigation', { name: 'Main' })).toHaveCount(0);
});
