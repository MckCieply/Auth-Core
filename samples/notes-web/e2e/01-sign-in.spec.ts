import { expect, test } from '@playwright/test';
import { setting } from './support/env';
import { openLogin, signIn, submitLogin } from './support/session';

const seed = () => ({ email: setting('E2E_SEED_EMAIL'), password: setting('E2E_SEED_PASSWORD') });

test('a page that needs a session sends an anonymous visitor to the sign-in, which comes back to it', async ({ page }) => {
  const { email, password } = seed();
  await page.goto('/notes');
  await expect(page).toHaveURL(/\/login\?returnUrl=%2Fnotes$/);
  await submitLogin(page, email, password);
  await expect(page).toHaveURL(/\/notes$/);
  await expect(page.getByTestId('me-email')).toHaveText(email);
});

test('an unknown path ends on the sign-in with /notes as the page to come back to', async ({ page }) => {
  await page.goto('/no/such/page');
  await expect(page).toHaveURL(/\/login\?returnUrl=%2Fnotes$/);
});

test('a wrong password shows the message and stays on the sign-in', async ({ page }) => {
  const { email } = seed();
  await openLogin(page);
  await submitLogin(page, email, 'not-the-password');
  await expect(page.getByText('Wrong email or password.')).toBeVisible();
  await expect(page).toHaveURL(/\/login$/);
});

test('the admin signs in, sees the header, the seeded note, and adds a note that is listed', async ({ page }) => {
  const { email, password } = seed();
  await signIn(page, email, password);
  await expect(page.getByTestId('me-company')).not.toBeEmpty();
  await expect(page.getByTestId('me-role')).toHaveText('admin');
  await expect(page.getByText(setting('E2E_SEEDED_NOTE'))).toBeVisible();

  const text = `a note written by the e2e test ${Date.now()}`;
  await page.getByLabel('New note').fill(text);
  await page.getByRole('button', { name: 'Add note' }).click();
  await expect(page.getByText(text)).toBeVisible();
  await expect(page.getByLabel('New note')).toHaveValue('');
});
