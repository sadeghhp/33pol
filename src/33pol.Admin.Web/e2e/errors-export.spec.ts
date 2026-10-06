import { expect, test } from '@playwright/test';
import { installAdminApiMocks, openPrimaryTab, signInThroughGate } from './fixtures/admin-api';

test.beforeEach(async ({ page }) => {
  await installAdminApiMocks(page);
  await signInThroughGate(page);
  await openPrimaryTab(page, 'Errors', '#panel-errors');
  await expect(page.getByText('Upstream refused connection')).toBeVisible();
});

test('exports errors as JSON download', async ({ page }) => {
  const exportRequest = page.waitForRequest(
    (req) => req.method() === 'GET' && req.url().includes('/admin/api/errors/export') && req.url().includes('format=json'),
  );
  const downloadPromise = page.waitForEvent('download');

  await page.getByRole('button', { name: 'Export JSON' }).click();

  const request = await exportRequest;
  expect(request.url()).toContain('format=json');

  const download = await downloadPromise;
  expect(download.suggestedFilename()).toMatch(/errors-export\.json$/);
  await expect(page.getByText(/export downloaded/i)).toBeVisible();
});

test('exports errors as CSV download', async ({ page }) => {
  const exportRequest = page.waitForRequest(
    (req) => req.method() === 'GET' && req.url().includes('/admin/api/errors/export') && req.url().includes('format=csv'),
  );
  const downloadPromise = page.waitForEvent('download');

  await page.getByRole('button', { name: 'Export CSV' }).click();

  const request = await exportRequest;
  expect(request.url()).toContain('format=csv');

  const download = await downloadPromise;
  expect(download.suggestedFilename()).toMatch(/errors-export\.csv$/);
});
