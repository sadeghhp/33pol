import { expect, test } from '@playwright/test';
import { installAdminApiMocks, signInThroughGate } from './fixtures/admin-api';

test('built admin shell loads and shows sign-in', async ({ page }) => {
  await installAdminApiMocks(page);
  await page.goto('./');
  await expect(page.locator('#root')).toBeVisible();
  await expect(page.getByRole('heading', { name: '33pol control plane' })).toBeVisible();
});

test('dialog closes on Escape after sign-in', async ({ page }) => {
  await installAdminApiMocks(page);
  await signInThroughGate(page);
  await page.getByRole('tab', { name: 'Keys' }).click();
  await page.getByRole('button', { name: 'Create key' }).click();
  await expect(page.getByRole('dialog')).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(page.getByRole('dialog')).toHaveCount(0);
});
