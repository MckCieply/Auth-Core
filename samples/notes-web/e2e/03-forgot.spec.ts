import { expect, test } from '@playwright/test';
import { acceptInviteByApi } from './support/api';
import { setting } from './support/env';
import { waitForLink } from './support/mail';
import { expectCleanAddressBar } from './support/page';
import { submitLogin } from './support/session';

// The server sends a mail within a minute or two: each wait is below the test's timeout, and the test's timeout covers both.
const MAIL_WAIT_MS = 100_000;

test('forgot, the link from the mail, a weak and then a good password, and a sign-in with it', async ({ page }) => {
  test.setTimeout(300_000);
  const email = setting('E2E_RESETTER_EMAIL');
  const oldPassword = setting('E2E_USER_PASSWORD');
  const newPassword = setting('E2E_NEW_PASSWORD');

  // The user is one the script invited, not the seeded admin: a person with no account has no password to forget.
  await acceptInviteByApi(await waitForLink(email, 'invite', MAIL_WAIT_MS), oldPassword);

  await page.goto('/forgot');
  await page.getByLabel('Email').fill(email);
  await page.getByRole('button', { name: 'Send the link' }).click();
  await expect(page.getByText('If an account exists for this address, we sent a link.')).toBeVisible();

  const link = await waitForLink(email, 'reset', MAIL_WAIT_MS);
  await page.goto(link);
  await expect(page.getByRole('heading', { name: 'Choose a new password' })).toBeVisible();
  expectCleanAddressBar(page);

  // A weak password: the rules it breaks, one line each; the token is still good afterwards.
  await page.getByLabel('New password').fill('short');
  await page.getByLabel('Repeat the password').fill('short');
  await page.getByRole('button', { name: 'Change password' }).click();
  const rules = page.getByTestId('rules');
  await expect(rules).toContainText('The password is too short.');
  await expect(rules).toContainText('The password needs an uppercase letter.');
  await expect(rules).toContainText('The password needs a digit.');
  await expect(rules).not.toContainText('lowercase');

  await page.getByLabel('New password').fill(newPassword);
  await page.getByLabel('Repeat the password').fill(newPassword);
  await page.getByRole('button', { name: 'Change password' }).click();
  await expect(page.getByText('Password changed.')).toBeVisible();
  await expect(page).toHaveURL(/\/reset$/);

  // No sign-in follows the reset: the person signs in with the new password.
  await page.getByRole('link', { name: 'Sign in.' }).click();
  await expect(page).toHaveURL(/\/login$/);
  await submitLogin(page, email, newPassword);
  await expect(page).toHaveURL(/\/notes$/);
  await expect(page.getByTestId('me-email')).toHaveText(email);

  // The link is used up.
  await page.goto(link);
  await page.getByLabel('New password').fill(newPassword);
  await page.getByLabel('Repeat the password').fill(newPassword);
  await page.getByRole('button', { name: 'Change password' }).click();
  await expect(page.getByText('This link has expired or was already used.')).toBeVisible();
  await expect(page.getByRole('link', { name: 'Ask for a new link' })).toBeVisible();

  // The old password no longer works (in the same test: a separate one would depend on this one having run).
  await page.goto('/login');
  await submitLogin(page, email, oldPassword);
  await expect(page.getByText('Wrong email or password.')).toBeVisible();
});
