import { expect, test } from '@playwright/test';
import { openPrimaryTab, signInThroughGate } from './fixtures/gateway-auth';

test('built admin shell loads and shows sign-in', async ({ page }) => {
  await page.goto('./');
  await expect(page.getByRole('heading', { name: '33pol control plane' })).toBeVisible();
  await expect(page.getByLabel('Admin API key')).toBeVisible();
});

test('dialog closes on Escape after sign-in', async ({ page }) => {
  await signInThroughGate(page);
  await openPrimaryTab(page, 'Keys', '#panel-keys');
  await page.getByRole('button', { name: 'Create key' }).click();
  await expect(page.getByRole('heading', { name: 'Create API key' })).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(page.getByRole('heading', { name: 'Create API key' })).toHaveCount(0);
});
