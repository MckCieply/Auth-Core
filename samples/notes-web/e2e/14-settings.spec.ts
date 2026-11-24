import { Page, expect, test } from '@playwright/test';
import { createCompany, deleteCompanyByApi, picker, signUpPrivatelyByApi } from './support/accounts';
import { setting } from './support/env';
import { signInAtFake, expectNoTicketInAddressBar } from './support/fake-provider';
import { openMailLink, waitForLink } from './support/mail';
import { signIn, signOut } from './support/session';
import { endConfirmationWindow, openSettings } from './support/settings';

// Spec 0012, criterion 30: the settings screen against the fake provider, on the stack of scripts/e2e-web.sh with the testing endpoint on.
const MAIL_WAIT_MS = 100_000;

function unique(prefix: string): string {
  return `${prefix}-${Date.now()}-${Math.floor(Math.random() * 1_000_000)}`;
}

/** A new person signs up with a provider as a private person, through the finish screen, and lands on the notes. */
async function signUpPrivately(page: Page, provider: 'Google' | 'GitHub', email: string): Promise<void> {
  await page.goto('/signup');
  await page.getByRole('link', { name: `Sign up with ${provider}` }).click();
  await signInAtFake(page, provider, email, true);
  await expect(page).toHaveURL(/\/provider-finish$/);
  expectNoTicketInAddressBar(page);
  await page.locator('label').filter({ hasText: 'Private person' }).click();
  await page.getByRole('button', { name: 'Create account' }).click();
  await expect(page).toHaveURL(/\/notes$/);
  await expect(page.getByTestId('me-email')).toHaveText(email);
}

test('a person signs up with GitHub, connects Google, sets a password by mail, disconnects GitHub after confirming, and changes the password', async ({ page }) => {
  test.setTimeout(300_000);
  const email = `${unique('settings')}@github.e2e.test`;
  const first = `E2e-First-${unique('p')}-A1`;
  const second = `E2e-Second-${unique('p')}-B2`;

  // GitHub is a button beside the others, and a new person signs up with it.
  await page.goto('/login');
  await expect(page.getByTestId('provider-buttons').getByRole('link')).toHaveText([
    'Continue with Google',
    'Continue with Microsoft',
    'Continue with GitHub',
  ]);
  await signUpPrivately(page, 'GitHub', email);

  // The settings: no password, GitHub connected; Google connects at once, without a confirmation (the sign-up opened the window).
  await openSettings(page);
  await expect(page.getByTestId('row-password')).toContainText('Not set');
  await expect(page.getByTestId('row-github')).toContainText('Connected');
  await page.getByTestId('connect-google').click();
  await signInAtFake(page, 'Google', email, true);
  await expect(page).toHaveURL(/\/settings$/);
  expectNoTicketInAddressBar(page);
  await expect(page.getByTestId('settings-notice')).toHaveText('Google connected.');
  await expect(page.getByTestId('row-google')).toContainText('Connected');
  await expect(page.getByTestId('confirm-dialog')).toHaveCount(0);

  // A first password through "Send me a link" and the reset mail; setting it signs out everywhere, so the person signs in with it.
  await page.getByTestId('send-link').click();
  await expect(page.getByTestId('link-sent')).toHaveText(
    'Check your inbox: the link sets your password. Setting it signs you out everywhere.',
  );
  const link = await waitForLink(email, 'reset', MAIL_WAIT_MS);
  await openMailLink(page, link);
  await page.getByLabel('New password', { exact: true }).fill(first);
  await page.getByLabel('Repeat the password').fill(first);
  await page.getByRole('button', { name: 'Change password' }).click();
  await expect(page.getByText('Password changed.')).toBeVisible();
  await signIn(page, email, first);

  // The window ended by the testing endpoint: "Disconnect" GitHub asks "Confirm it's you" first, with the password.
  await openSettings(page);
  await expect(page.getByTestId('row-password')).toContainText('Set');
  await endConfirmationWindow(page);
  await page.getByTestId('disconnect-github').click();
  const dialog = page.getByTestId('confirm-dialog');
  await expect(dialog.getByRole('heading', { name: "Confirm it's you" })).toBeVisible();
  await dialog.getByLabel('Password').fill(first);
  await dialog.getByRole('button', { name: 'Confirm' }).click();
  await expect(page.getByTestId('settings-notice')).toHaveText('GitHub disconnected.');
  await expect(page.getByTestId('connect-github')).toBeVisible();
  await expect(page.getByTestId('disconnect-github')).toHaveCount(0);

  // The password changed with the current one; the new one signs in after.
  await page.locator('#current-password').fill(first);
  await page.locator('#settings-new-password').fill(second);
  await page.locator('#settings-repeat-password').fill(second);
  await page.getByTestId('password-submit').click();
  await expect(page.getByTestId('password-message')).toHaveText('Password changed.');
  await page.goto('/notes');
  await signOut(page);
  await signIn(page, email, second);
});

test('a person with only one provider sees "Disconnect" disabled, with the sentence', async ({ page }) => {
  test.setTimeout(120_000);
  const email = `${unique('single')}@gmail.e2e.test`;
  await signUpPrivately(page, 'Google', email);

  await openSettings(page);
  await expect(page.getByTestId('disconnect-google')).toBeDisabled();
  await expect(page.getByTestId('disconnect-blocked')).toHaveText(
    'Set a password or connect another provider before you disconnect this one.',
  );
});

test('an admin without a password deletes the company with "Confirm with Google"', async ({ page }) => {
  test.setTimeout(180_000);
  const email = `${unique('owner')}@gmail.e2e.test`;
  const company = `Firma ${unique('c')}`;

  await page.goto('/signup');
  await page.getByRole('link', { name: 'Sign up with Google' }).click();
  await signInAtFake(page, 'Google', email, true);
  await expect(page).toHaveURL(/\/provider-finish$/);
  await page.locator('label').filter({ hasText: 'Organization' }).click();
  await page.getByLabel('Company name').fill(company);
  await page.getByRole('button', { name: 'Create account' }).click();
  await expect(page).toHaveURL(/\/notes$/);
  await expect(page.getByTestId('me-company')).toHaveText(company);

  await page.getByTestId('team-link').click();
  await expect(page).toHaveURL(/\/team$/);
  await page.getByTestId('delete-company').click();
  await expect(page.locator('#company-password')).toHaveCount(0);
  await page.getByRole('link', { name: 'Confirm with Google' }).click();
  await signInAtFake(page, 'Google', email, true);

  // Back on the team screen, ready to confirm; the ticket is in no URL.
  await expect(page).toHaveURL(/\/team$/);
  expectNoTicketInAddressBar(page);
  await page.locator('#company-name').fill('not the name');
  await page.getByTestId('delete-company-confirm').click();
  await expect(page.getByTestId('delete-company-error')).toHaveText("Type the company's name exactly.");
  await page.locator('#company-name').fill(company);
  await page.getByTestId('delete-company-confirm').click();
  await expect(page).toHaveURL(/\/login$/);
  await expect(page.getByTestId('login-notice')).toHaveText('The company was deleted.');

  // The account went with the company: the same Google identity is a new person now.
  // the company was the account's only space
  await page.getByRole('link', { name: 'Continue with Google' }).click();
  await signInAtFake(page, 'Google', email, true);
  await expect(page).toHaveURL(/\/provider-finish$/);
});

/**
 * A private person with a password, made by the API, signs in and creates a company with the picker: the account is in a company, switched into it.
 * Each test removes the company by the API in a `finally`, with the password in force.
 */
async function inCompany(page: Page): Promise<{ email: string; name: string; password: string }> {
  const email = `${unique('member')}@e2e.test`;
  const password = setting('E2E_USER_PASSWORD');
  const name = `Solo ${unique('c')}`;
  await signUpPrivatelyByApi(email, password, MAIL_WAIT_MS);
  await signIn(page, email, password);
  await expect(page.getByTestId('me-private')).toHaveText('Private account');
  return { email, name, password };
}

/** Deletes the company by the API with the first password that signs in (a 401 tries the next one). */
async function removeCompany(email: string, passwords: string[], name: string): Promise<void> {
  for (const [index, candidate] of passwords.entries()) {
    try {
      await deleteCompanyByApi(email, candidate, name);
      return;
    } catch (error) {
      if (index === passwords.length - 1 || !String(error).includes('sign in by the API: 401')) {
        throw error;
      }
    }
  }
}

test('after a password change in a switched company the person is still in that company, with the window open', async ({ page }) => {
  test.setTimeout(300_000);
  const { email, name, password } = await inCompany(page);
  const changed = `E2e-Changed-${unique('p')}-C3`;
  let failure: unknown;
  try {
    await createCompany(page, name);
    await expect(page.getByTestId('me-company')).toHaveText(name);

    await openSettings(page);
    await page.locator('#current-password').fill(password);
    await page.locator('#settings-new-password').fill(changed);
    await page.locator('#settings-repeat-password').fill(changed);
    await page.getByTestId('password-submit').click();
    await expect(page.getByTestId('password-message')).toHaveText('Password changed.');

    // The session stays in the company it was switched into, not "Private account".
    await page.goto('/notes');
    await expect(page.getByTestId('me-company')).toHaveText(name);
    await expect(picker(page).locator('option:checked')).toHaveText(name);
  } catch (error) {
    failure = error;
  }
  // The clean-up runs whatever happened; the change may have been applied even when its message did not show, so the new password is tried too.
  try {
    await removeCompany(email, [password, changed], name);
  } catch (error) {
    failure ??= error;
  }
  if (failure !== undefined) {
    throw failure;
  }
});

test('a person who is in a company sees the company_member text on /settings and no button', async ({ page }) => {
  test.setTimeout(300_000);
  const { email, name, password } = await inCompany(page);
  let failure: unknown;
  try {
    await createCompany(page, name);
    await openSettings(page);
    await expect(page.getByTestId('delete-company-member')).toContainText(`You are still a member of ${name}.`);
    await expect(page.getByTestId('delete-account')).toHaveCount(0);
  } catch (error) {
    failure = error;
  }
  try {
    await removeCompany(email, [password], name);
  } catch (error) {
    failure ??= error;
  }
  if (failure !== undefined) {
    throw failure;
  }
});
