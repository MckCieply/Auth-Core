import { expect, test } from '@playwright/test';
import { acceptInviteByApi } from './support/api';
import { inviteByApi, loginStatus, signUpPrivatelyByApi } from './support/accounts';
import { setting } from './support/env';
import { openMailLink, waitForLink } from './support/mail';
import { expectCleanAddressBar, expectNoEmailInAddressBar } from './support/page';
import { signIn } from './support/session';

// The request and the API invitation wake the server's dispatcher, so a mail comes within seconds. Each wait is below the test's timeout.
const MAIL_WAIT_MS = 100_000;
const SENT = 'Check your inbox. We sent a message to this address.';

function unique(prefix: string): string {
  return `${prefix}-${Date.now()}-${Math.floor(Math.random() * 1_000_000)}`;
}

test('a private person signs up through the mail, works alone, deletes the account and is back on the sign-in screen', async ({ page }) => {
  test.setTimeout(240_000);
  const email = `${unique('private')}@e2e.test`;
  const password = setting('E2E_USER_PASSWORD');
  const note = `Private note ${unique('n')}`;

  // Both kinds are on (samples/notes-api/auth.yaml): the sign-up screen offers the choice, the organization first.
  await page.goto('/login');
  await page.getByRole('link', { name: 'Create an account' }).click();
  await expect(page).toHaveURL(/\/signup$/);
  await expect(page.getByTestId('signup-kind')).toBeVisible();
  await expect(page.getByLabel('Organization')).toBeChecked();
  await expect(page.getByLabel('Company name')).toBeVisible();
  // The choice is two small radio buttons, not two fields as wide as the form.
  for (const name of ['Organization', 'Private person']) {
    const box = await page.getByLabel(name).boundingBox();
    expect(box, `the radio button "${name}" has no box`).not.toBeNull();
    expect(box !== null && box.width < 40 && box.height < 40, `the radio button "${name}" is not small`).toBe(true);
  }
  // A click on the text of a label chooses its radio button, and the company name leaves the form.
  await page.locator('label').filter({ hasText: 'Private person' }).click();
  await expect(page.getByLabel('Private person')).toBeChecked();
  await expect(page.getByLabel('Organization')).not.toBeChecked();
  await expect(page.getByLabel('Company name')).toHaveCount(0);
  await page.getByLabel('Email').fill(email);
  await page.getByRole('button', { name: 'Create account' }).click();
  await expect(page.getByTestId('signup-sent')).toHaveText(SENT);
  expectNoEmailInAddressBar(page);

  // The link opens the private completion: the address, the password twice, no company.
  const link = await waitForLink(email, 'complete-signup', MAIL_WAIT_MS);
  await openMailLink(page, link);
  await expect(page).toHaveURL(/\/complete-signup$/);
  expectCleanAddressBar(page);
  await expect(page.getByLabel('Email')).toHaveValue(email);
  await expect(page.getByLabel('Company name')).toHaveCount(0);
  await page.getByLabel('New password').fill(password);
  await page.getByLabel('Repeat the password').fill(password);
  await page.getByRole('button', { name: 'Create account' }).click();

  // Signed in at once, alone: "Private account", no company, no Team.
  await expect(page).toHaveURL(/\/notes$/);
  await expect(page.getByTestId('me-email')).toHaveText(email);
  await expect(page.getByTestId('me-private')).toHaveText('Private account');
  await expect(page.getByTestId('me-company')).toHaveCount(0);
  await expect(page.getByTestId('team-link')).toHaveCount(0);
  await page.getByLabel('New note').fill(note);
  await page.getByRole('button', { name: 'Add note' }).click();
  await expect(page.getByText(note)).toBeVisible();

  // "Delete account": the warning, the password, and the sign-in screen with the notice.
  await page.getByTestId('delete-account').click();
  await expect(page.getByTestId('delete-warning')).toHaveText('This deletes your account and all your notes. It cannot be undone.');
  await page.locator('#delete-password').fill(password);
  await page.getByRole('button', { name: 'Delete my account' }).click();
  await expect(page).toHaveURL(/\/login$/);
  await expect(page.getByTestId('login-notice')).toHaveText('Your account was deleted.');
  expect(await loginStatus(email, password), 'the deleted account still signs in').toBe(401);
});

test('a private person accepts an invitation through the warning and signs in to the company', async ({ page }) => {
  test.setTimeout(240_000);
  const email = `${unique('moving')}@e2e.test`;
  const password = setting('E2E_USER_PASSWORD');
  await signUpPrivatelyByApi(email, password, MAIL_WAIT_MS);
  await inviteByApi(email, 'user');

  const link = await waitForLink(email, 'invite', MAIL_WAIT_MS);
  await openMailLink(page, link);
  await expect(page).toHaveURL(/\/invite$/);
  expectCleanAddressBar(page);
  await expect(page.getByTestId('invite-private-warning')).toContainText('You already have a private account with this email address.');
  await expect(page.getByLabel('New password')).toHaveCount(0);
  await page.getByRole('button', { name: 'Move me to the company and delete my private account' }).click();

  // The account keeps its password: the sign-in screen, with the address filled in, signs it in to the company.
  await expect(page).toHaveURL(/\/login$/);
  await expect(page.getByLabel('Email')).toHaveValue(email);
  await page.getByLabel('Password', { exact: true }).fill(password);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page).toHaveURL(/\/notes$/);
  await expect(page.getByTestId('me-email')).toHaveText(email);
  await expect(page.getByTestId('me-private')).toHaveCount(0);
  await expect(page.getByTestId('me-company')).toBeVisible();
  await expect(page.getByTestId('me-role')).toHaveText('user');
});

test('an admin removes a member on /team after the confirmation that names the deletion', async ({ page }) => {
  test.setTimeout(240_000);
  const email = `${unique('member')}@e2e.test`;
  const password = setting('E2E_USER_PASSWORD');
  await inviteByApi(email, 'user');
  await acceptInviteByApi(await waitForLink(email, 'invite', MAIL_WAIT_MS), password);

  await signIn(page, setting('E2E_SEED_EMAIL'), setting('E2E_SEED_PASSWORD'));
  await page.getByTestId('team-link').click();
  await expect(page).toHaveURL(/\/team$/);
  const row = page.getByTestId('member').filter({ hasText: email });
  await row.getByTestId('remove').click();
  await expect(row.getByTestId('remove-confirmation')).toHaveText("This person's account will be deleted.");
  await row.getByTestId('remove-confirm').click();
  await expect(page.getByTestId('member').filter({ hasText: email })).toHaveCount(0);
  expect(await loginStatus(email, password), 'the removed member still signs in').toBe(401);
});
