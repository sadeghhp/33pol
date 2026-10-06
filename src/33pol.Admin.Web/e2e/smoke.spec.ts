import { expect, test } from '@playwright/test';

test('admin shell loads and shows sign-in', async ({ page }) => {
  await page.goto('/admin/');
  await expect(page.locator('#root')).toBeVisible();
  await expect(page.getByRole('heading', { name: /33pol control plane|gateway control plane|sign in|connect/i })).toBeVisible();
});

test('dialog closes on Escape', async ({ page }) => {
  await page.goto('/admin/');
  const connect = page.getByRole('button', { name: /connect|change key|sign in/i }).first();
  if (await connect.isVisible()) {
    await connect.click();
  }
  const dialog = page.getByRole('dialog');
  if (await dialog.isVisible()) {
    await page.keyboard.press('Escape');
    await expect(dialog).toBeHidden();
  }
});
