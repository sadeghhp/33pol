import { defineConfig } from '@playwright/test';

const port = Number(process.env.ADMIN_E2E_PORT ?? 4173);
const host = process.env.ADMIN_E2E_HOST ?? '127.0.0.1';
const baseURL = `http://${host}:${port}/admin/`;

export default defineConfig({
  testDir: './e2e',
  timeout: 45_000,
  expect: { timeout: 10_000 },
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? [['list'], ['html', { open: 'never' }]] : 'list',
  use: {
    baseURL,
    trace: 'on-first-retry',
    screenshot: 'only-on-failure',
  },
  webServer: {
    command: `npm run preview -- --host ${host} --port ${port}`,
    port,
    reuseExistingServer: !process.env.CI,
    timeout: 120_000,
  },
});
