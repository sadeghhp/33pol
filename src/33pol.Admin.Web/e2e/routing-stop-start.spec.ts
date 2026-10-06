import { expect, test } from '@playwright/test';
import { openPrimaryTab, signInThroughGate } from './fixtures/gateway-auth';

test.beforeEach(async ({ page }) => {
  await signInThroughGate(page);
  await openPrimaryTab(page, 'Routing', '#panel-routing');
});

test('stops a serving model after confirmation', async ({ page }) => {
  const rows = page.locator('#panel-routing tbody tr');
  if ((await rows.count()) === 0) test.skip();

  const row = rows.first();
  const stopBtn = row.getByRole('button', { name: 'Stop model' });
  if ((await stopBtn.count()) === 0) test.skip();

  await stopBtn.click();
  await expect(page.getByRole('heading', { name: 'Stop model?' })).toBeVisible();
  await page.getByRole('button', { name: 'Stop' }).click();
  await expect(page.locator('.toast')).toContainText(/stop/i);
});

test('starts a stopped model after confirmation', async ({ page }) => {
  const rows = page.locator('#panel-routing tbody tr');
  if ((await rows.count()) === 0) test.skip();

  const row = rows.first();
  const startBtn = row.getByRole('button', { name: 'Start model' });
  if ((await startBtn.count()) === 0) test.skip();

  await startBtn.click();
  await expect(page.getByRole('heading', { name: 'Start model?' })).toBeVisible();
  await page.getByRole('button', { name: 'Start' }).click();
  await expect(page.locator('.toast')).toContainText(/start/i);
});
