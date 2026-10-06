import { expect, test } from '@playwright/test';
import { installAdminApiMocks, openPrimaryTab, signInThroughGate } from './fixtures/admin-api';

test.beforeEach(async ({ page }) => {
  await installAdminApiMocks(page);
  await signInThroughGate(page);
  const modelsLoaded = page.waitForResponse(
    (response) => /\/admin\/api\/models$/.test(new URL(response.url()).pathname) && response.ok(),
  );
  await openPrimaryTab(page, 'Routing', '#panel-routing');
  await modelsLoaded;
  await expect(page.locator('#panel-routing').getByText('local-mock')).toBeVisible();
});

test('stops a serving model after confirmation', async ({ page }) => {
  const stopRequest = page.waitForRequest(
    (req) => req.method() === 'POST' && req.url().includes('/admin/api/models/local-mock/stop'),
  );

  await page.getByRole('button', { name: 'Stop model' }).click();
  await expect(page.getByRole('heading', { name: 'Stop model?' })).toBeVisible();
  await page.getByRole('button', { name: 'Stop', exact: true }).click();

  const request = await stopRequest;
  expect(request.url()).toContain('/admin/api/models/local-mock/stop');
  await expect(page.getByText(/model "local-mock" stopped/i)).toBeVisible();
});

test('starts a stopped model after confirmation', async ({ page }) => {
  await page.route(/\/admin\/api\//, async (route) => {
    const url = new URL(route.request().url());
    if (url.pathname === '/admin/api/models' && route.request().method() === 'GET') {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify([
          {
            id: 'local-mock',
            modelType: 'mock',
            url: 'http://127.0.0.1/mock',
            state: 'stopped',
            publicAccess: true,
            hasUpstreamCredential: false,
            pricing: null,
            aliases: [],
          },
        ]),
      });
      return;
    }
    await route.fallback();
  });

  const modelsLoaded = page.waitForResponse(
    (response) => /\/admin\/api\/models$/.test(new URL(response.url()).pathname) && response.ok(),
  );
  await page.locator('#panel-routing .filter-row button').first().click();
  await modelsLoaded;
  await expect(page.getByText('Stopped')).toBeVisible();

  const startRequest = page.waitForRequest(
    (req) => req.method() === 'POST' && req.url().includes('/admin/api/models/local-mock/start'),
  );

  await page.getByRole('button', { name: 'Start model' }).click();
  await expect(page.getByRole('heading', { name: 'Start model?' })).toBeVisible();
  await page.getByRole('button', { name: 'Start', exact: true }).click();

  const request = await startRequest;
  expect(request.url()).toContain('/admin/api/models/local-mock/start');
  await expect(page.getByText(/model "local-mock" started/i)).toBeVisible();
});
