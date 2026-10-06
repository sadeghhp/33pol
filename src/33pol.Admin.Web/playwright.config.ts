import { defineConfig } from '@playwright/test';

const port = Number(process.env.ADMIN_E2E_PORT ?? 5050);
const host = process.env.ADMIN_E2E_HOST ?? '127.0.0.1';
const baseURL = `http://${host}:${port}/admin/`;

export default defineConfig({
  testDir: './e2e',
  timeout: 60_000,
  expect: { timeout: 15_000 },
  fullyParallel: false,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  workers: 1,
  reporter: process.env.CI ? [['list'], ['html', { open: 'never' }]] : 'list',
  use: {
    baseURL,
    trace: 'on-first-retry',
    screenshot: 'only-on-failure',
  },
  webServer: {
    command: 'bash ../../build/run-admin-e2e-gateway.sh',
    url: `http://${host}:${port}/health/ready`,
    reuseExistingServer: !process.env.CI,
    timeout: 120_000,
  },
});
