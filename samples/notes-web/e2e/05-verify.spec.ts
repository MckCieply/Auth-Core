import { expect, test } from '@playwright/test';
import { setting } from './support/env';
import { waitForLink } from './support/mail';
import { expectCleanAddressBar } from './support/page';
import { openLogin, submitLogin } from './support/session';

test('the unverified seeded user is told, sends the link again, confirms from the mail and signs in', async ({ page }) => {
  test.setTimeout(120_000);
  const email = setting('E2E_UNVERIFIED_EMAIL');
  const password = setting('E2E_UNVERIFIED_PASSWORD');

  await openLogin(page);
  await submitLogin(page, email, password);
  await expect(page.getByText('Your email address is not confirmed yet.')).toBeVisible();
  await expect(page).toHaveURL(/\/login$/);

  await page.getByRole('button', { name: 'Send the verification link again' }).click();
  await expect(page.getByText('If this address needs confirming, we sent a new link.')).toBeVisible();

  // The wait is below the test's timeout, so a missing mail fails with its own message.
  await page.goto(await waitForLink(email, 'verify', 100_000));
  await expect(page.getByText('Email confirmed.')).toBeVisible();
  expectCleanAddressBar(page);

  await page.getByRole('link', { name: 'Sign in.' }).click();
  await expect(page).toHaveURL(/\/login$/);
  await submitLogin(page, email, password);
  await expect(page).toHaveURL(/\/notes$/);
  await expect(page.getByTestId('me-email')).toHaveText(email);
});
