import { Page, expect, test } from '@playwright/test';
import { inviteByApi, signUpPrivatelyByApi } from './support/accounts';
import { setting } from './support/env';
import { cancelAtFake, chooseAtFake, expectNoTicketInAddressBar, signInAtFake } from './support/fake-provider';
import { openMailLink, waitForLink } from './support/mail';
import { expectCleanAddressBar } from './support/page';
import { signOut } from './support/session';

// Spec 0011, criterion 32: the provider buttons against the fake provider, which Auth-Core trusts as its test issuer on this stack.
const MAIL_WAIT_MS = 100_000;

function unique(prefix: string): string {
  return `${prefix}-${Date.now()}-${Math.floor(Math.random() * 1_000_000)}`;
}

/** A new person signs up privately with Google, through the finish screen, and lands on the notes. */
async function signUpWithGoogle(page: Page, email: string): Promise<void> {
  await page.goto('/signup');
  await page.getByRole('link', { name: 'Sign up with Google' }).click();
  await signInAtFake(page, 'Google', email, true);
  await expect(page).toHaveURL(/\/provider-finish$/);
  expectNoTicketInAddressBar(page);
  await expect(page.getByRole('heading', { name: 'Finish creating your account' })).toBeVisible();
  await expect(page.getByLabel('Email')).toHaveValue(email);
  await page.locator('label').filter({ hasText: 'Private person' }).click();
  await expect(page.getByLabel('Company name')).toHaveCount(0);
  await page.getByRole('button', { name: 'Create account' }).click();
  await expect(page).toHaveURL(/\/notes$/);
  expectNoTicketInAddressBar(page);
  await expect(page.getByTestId('me-email')).toHaveText(email);
  await expect(page.getByTestId('me-private')).toHaveText('Private account');
}

test('a new person signs up privately with Google, signs out, and signs in again with the button alone', async ({ page }) => {
  test.setTimeout(120_000);
  const email = `${unique('google')}@gmail.e2e.test`;

  // The buttons come first on the sign-in screen, in the order of the manifest, then "or" and the form.
  await page.goto('/login');
  const buttons = page.getByTestId('provider-buttons').getByRole('link');
  await expect(buttons).toHaveText(['Continue with Google', 'Continue with Microsoft']);
  await expect(page.getByTestId('provider-or')).toHaveText('or');
  const lastButton = await buttons.last().boundingBox();
  const separator = await page.getByTestId('provider-or').boundingBox();
  const emailField = await page.getByLabel('Email').boundingBox();
  expect(lastButton !== null && separator !== null && emailField !== null, 'a part of the sign-in screen has no box').toBe(true);
  expect(
    lastButton !== null && separator !== null && emailField !== null && lastButton.y < separator.y && separator.y < emailField.y,
    'the buttons, "or" and the form are not in this order',
  ).toBe(true);

  await signUpWithGoogle(page, email);
  await signOut(page);

  await page.getByRole('link', { name: 'Continue with Google' }).click();
  await signInAtFake(page, 'Google', email, true);
  await expect(page).toHaveURL(/\/notes$/);
  expectNoTicketInAddressBar(page);
  await expect(page.getByTestId('me-email')).toHaveText(email);
});

test('a person with a password account links Microsoft with the password, then signs in with the button alone', async ({ page }) => {
  test.setTimeout(240_000);
  const email = `${unique('linker')}@e2e.test`;
  const password = setting('E2E_USER_PASSWORD');
  await signUpPrivatelyByApi(email, password, MAIL_WAIT_MS);

  await page.goto('/login');
  await page.getByRole('link', { name: 'Continue with Microsoft' }).click();
  await signInAtFake(page, 'Microsoft', email, true);
  await expect(page).toHaveURL(/\/provider-link$/);
  expectNoTicketInAddressBar(page);
  await expect(page.getByTestId('link-intro')).toHaveText(
    `An account with ${email} exists. Enter its password once to connect Microsoft. After that Microsoft alone signs you in.`,
  );
  await page.getByLabel('Password', { exact: true }).fill(`${password}-wrong`);
  await page.getByRole('button', { name: 'Connect and sign in' }).click();
  await expect(page.getByTestId('link-error')).toHaveText("Wrong password. No password yet? Use 'Forgot your password?' to set one.");
  await page.getByLabel('Password', { exact: true }).fill(password);
  await page.getByRole('button', { name: 'Connect and sign in' }).click();
  await expect(page).toHaveURL(/\/notes$/);
  await expect(page.getByTestId('me-email')).toHaveText(email);

  await signOut(page);
  await page.getByRole('link', { name: 'Continue with Microsoft' }).click();
  await signInAtFake(page, 'Microsoft', email, true);
  await expect(page).toHaveURL(/\/notes$/);
  expectNoTicketInAddressBar(page);
  await expect(page.getByTestId('me-email')).toHaveText(email);
});

test('a person with an unverified Microsoft work address sees the link screen with the sign-up sentence', async ({ page }) => {
  await page.goto('/login');
  await page.getByRole('link', { name: 'Continue with Microsoft' }).click();
  await chooseAtFake(page, 'Microsoft', 'Bo, a work account (bo@firma.test, not verified)');
  await expect(page).toHaveURL(/\/provider-link$/);
  expectNoTicketInAddressBar(page);
  await expect(page.getByTestId('link-intro')).toHaveText(
    'If you have an account with bo@firma.test, enter its password once to connect Microsoft. If you do not, create an account with your email address.',
  );
  await expect(page.getByTestId('link-signup')).toHaveAttribute('href', '/signup');
});

test('an invited person accepts with Google and lands in the company', async ({ page }) => {
  test.setTimeout(240_000);
  const email = `${unique('invited')}@gmail.e2e.test`;
  await inviteByApi(email, 'user');
  await openMailLink(page, await waitForLink(email, 'invite', MAIL_WAIT_MS));
  await expect(page).toHaveURL(/\/invite$/);
  expectCleanAddressBar(page);
  await page.getByRole('link', { name: 'Accept with Google' }).click();
  await signInAtFake(page, 'Google', email, true);
  await expect(page).toHaveURL(/\/notes$/);
  expectNoTicketInAddressBar(page);
  await expect(page.getByTestId('me-email')).toHaveText(email);
  await expect(page.getByTestId('me-company')).toBeVisible();
  await expect(page.getByTestId('me-role')).toHaveText('user');
});

test('a person who cancels at the provider sees the message and the way back', async ({ page }) => {
  await page.goto('/login');
  await page.getByRole('link', { name: 'Continue with Google' }).click();
  await cancelAtFake(page, 'Google');
  await expect(page).toHaveURL(/\/provider-return$/);
  expectNoTicketInAddressBar(page);
  await expect(page.getByTestId('provider-message')).toHaveText('Sign-in was cancelled.');
  await page.getByTestId('provider-back').click();
  await expect(page).toHaveURL(/\/login$/);
});

test('a private person without a password deletes the account with "Confirm with Google"', async ({ page }) => {
  test.setTimeout(120_000);
  const email = `${unique('deleter')}@gmail.e2e.test`;
  await signUpWithGoogle(page, email);
  const note = `Note ${unique('n')}`;
  await page.getByLabel('New note').fill(note);
  await page.getByRole('button', { name: 'Add note' }).click();
  await expect(page.getByText(note)).toBeVisible();

  await page.getByTestId('delete-account').click();
  await expect(page.getByTestId('delete-warning')).toHaveText('This deletes your account and all your notes. It cannot be undone.');
  await expect(page.locator('#delete-password')).toHaveCount(0);
  await page.getByRole('link', { name: 'Confirm with Google' }).click();
  await signInAtFake(page, 'Google', email, true);

  // Back on the notes with the section open, ready to confirm; the ticket is in no URL.
  await expect(page).toHaveURL(/\/notes$/);
  expectNoTicketInAddressBar(page);
  await expect(page.getByTestId('delete-section')).toBeVisible();
  await page.getByRole('button', { name: 'Delete my account' }).click();
  await expect(page).toHaveURL(/\/login$/);
  await expect(page.getByTestId('login-notice')).toHaveText('Your account was deleted.');

  // The account is gone: the same button and the same address now lead to the finish screen of a new person, not to the notes.
  await page.getByRole('link', { name: 'Continue with Google' }).click();
  await signInAtFake(page, 'Google', email, true);
  await expect(page).toHaveURL(/\/provider-finish$/);
  expectNoTicketInAddressBar(page);
});
