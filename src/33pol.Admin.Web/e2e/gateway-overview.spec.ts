import { expect, test } from '@playwright/test';
import { signInThroughGate } from './fixtures/gateway-auth';

test.beforeEach(async ({ page }) => {
  await signInThroughGate(page);
});

test('renders overview shell and toggles pause', async ({ page }) => {
  await expect(page.locator('#panel-dashboard')).toBeVisible();
  const onboarding = page.getByText('No traffic yet');
  const vitals = page.getByText('Requests');
  await expect(onboarding.or(vitals)).toBeVisible();

  const pauseBtn = page.getByRole('button', { name: 'Pause' });
  await pauseBtn.click();
  await expect(page.getByRole('button', { name: 'Resume' })).toBeVisible();
});
