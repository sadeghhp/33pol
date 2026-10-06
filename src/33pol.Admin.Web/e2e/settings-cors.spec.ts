import { expect, test } from '@playwright/test';
import { installAdminApiMocks, openPrimaryTab, signInThroughGate } from './fixtures/admin-api';

test.beforeEach(async ({ page }) => {
  await installAdminApiMocks(page);
  await signInThroughGate(page);
  await openPrimaryTab(page, 'Settings', '#panel-settings');
  await page.getByRole('tab', { name: 'CORS' }).click();
  await expect(page.getByRole('heading', { name: 'CORS allowed origins' })).toBeVisible();
});

test('loads, edits, and saves CORS origins', async ({ page }) => {
  await expect(page.getByPlaceholder('https://example.com')).toHaveValue('https://example.com');

  const saveRequest = page.waitForRequest(
    (req) => req.method() === 'PUT' && req.url().includes('/admin/api/cors'),
  );

  await page.getByRole('button', { name: 'Add origin' }).click();
  const inputs = page.getByPlaceholder('https://example.com');
  await inputs.nth(1).fill('https://app.test');
  await page.getByRole('button', { name: 'Save CORS' }).click();

  const request = await saveRequest;
  const payload = request.postDataJSON() as { allowedOrigins?: string[] };
  expect(payload.allowedOrigins).toEqual(['https://example.com', 'https://app.test']);
  await expect(page.getByText(/cors origins saved/i)).toBeVisible();
});
