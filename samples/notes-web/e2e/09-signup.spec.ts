import { expect, test } from '@playwright/test';
import { acceptInviteByApi } from './support/api';
import { setting } from './support/env';
import { openMailLink, waitForAccountExistsMail, waitForLink } from './support/mail';
import { expectCleanAddressBar, expectNoEmailInAddressBar } from './support/page';
import { signIn, signOut } from './support/session';

// The request wakes the server's dispatcher, so a mail comes within seconds; an invitation queued by the operator CLI waits for the poll
// (a minute or two). Each wait is below the test's timeout.
const MAIL_WAIT_MS = 100_000;
const SENT = 'Check your inbox. We sent a message to this address.';
const LINK_USED_UP = 'This link has expired or was already used.';

function unique(prefix: string): string {
  return `${prefix}-${Date.now()}-${Math.floor(Math.random() * 1_000_000)}`;
}

test('a visitor signs up, opens the mail link, finishes, is signed in at once and is the admin of a company of their own', async ({ page }) => {
  test.setTimeout(240_000);
  const email = `${unique('signup')}@e2e.test`;
  const typedCompany = `Typed ${unique('co')}`;
  const company = `Final ${unique('co')}`;
  const password = setting('E2E_USER_PASSWORD');
  // The address of every request the page makes: the person's address is in a body only, never in an address (criterion 22).
  const requested: string[] = [];
  page.on('request', (request) => requested.push(request.url()));
  // The address of the page after every navigation, the router's included: the person must not pass through the sign-in screen
  // after finishing (Amendment 2 of spec 0009).
  const urls: string[] = [];
  page.on('framenavigated', (frame) => {
    if (frame === page.mainFrame()) {
      urls.push(frame.url());
    }
  });

  // The sign-in screen offers the way in because Auth-Core says that sign-up is on.
  await page.goto('/login');
  await page.getByRole('link', { name: 'Create an account' }).click();
  await expect(page).toHaveURL(/\/signup$/);
  await page.getByLabel('Email').fill(email);
  await page.getByLabel('Company name').fill(typedCompany);
  await page.getByRole('button', { name: 'Create account' }).click();
  await expect(page.getByTestId('signup-sent')).toHaveText(SENT);
  expectNoEmailInAddressBar(page);

  // The link of the mail opens the completion screen; the token leaves the address bar at once.
  const link = await waitForLink(email, 'complete-signup', MAIL_WAIT_MS);
  await openMailLink(page, link);
  await expect(page).toHaveURL(/\/complete-signup$/); // the token has been taken out of the address
  await expect(page.getByRole('heading', { name: 'Finish creating your account' })).toBeVisible();
  expectCleanAddressBar(page);
  await expect(page.getByLabel('Email')).toHaveValue(email);
  await expect(page.getByLabel('Email')).not.toBeEditable();
  await expect(page.getByLabel('Company name')).toHaveValue(typedCompany);

  // A weak password: the rules it breaks, one line each; the link is still good afterwards.
  await page.getByLabel('New password').fill('short');
  await page.getByLabel('Repeat the password').fill('short');
  await page.getByRole('button', { name: 'Create company' }).click();
  const rules = page.getByTestId('rules');
  await expect(rules).toContainText('The password is too short.');
  await expect(rules).toContainText('The password needs an uppercase letter.');
  await expect(rules).toContainText('The password needs a digit.');

  // The name of the request was a suggestion: the person changes it, chooses the password and finishes.
  await page.getByLabel('Company name').fill(company);
  await page.getByLabel('New password').fill(password);
  await page.getByLabel('Repeat the password').fill(password);
  const beforeFinishing = urls.length;
  await page.getByRole('button', { name: 'Create company' }).click();

  // The completion signed the person in: the notes screen opens at once, with no sign-in screen on the way and the address in no URL.
  await expect(page).toHaveURL(/\/notes$/);
  expect(
    urls.slice(beforeFinishing).some((url) => /\/login(\?|$)/.test(url)),
    'the person went through the sign-in screen',
  ).toBe(false);
  // ...and the test did see the navigations, so that the line above says something.
  expect(
    urls.slice(beforeFinishing).some((url) => /\/notes$/.test(url)),
    'the notes screen was never reached by a navigation the test saw',
  ).toBe(true);
  expectNoEmailInAddressBar(page);
  await expect(page.getByTestId('me-email')).toHaveText(email);
  await expect(page.getByTestId('me-company')).toHaveText(company);
  await expect(page.getByTestId('me-role')).toHaveText('admin');
  // A company of their own: not the notes of the seeded company.
  await expect(page.getByText(setting('E2E_SEEDED_NOTE'))).toHaveCount(0);
  await expect(page.getByText('No notes yet.')).toBeVisible();

  // The refresh cookie came with the answer of the completion: a reload keeps the person signed in.
  await page.reload();
  await expect(page).toHaveURL(/\/notes$/);
  await expect(page.getByTestId('me-email')).toHaveText(email);
  await expect(page.getByTestId('me-company')).toHaveText(company);

  // The link is used up.
  await openMailLink(page, link);
  await expect(page.getByText(LINK_USED_UP)).toBeVisible();
  await page.getByRole('link', { name: 'Sign up again' }).click();
  await expect(page).toHaveURL(/\/signup$/);

  // The address was in no URL the page asked for, plain or encoded.
  expect(requested.length, 'no request was seen').toBeGreaterThan(5);
  expect(
    requested.some((url) => url.includes(email) || url.includes(encodeURIComponent(email)) || url.includes('%40')),
    'the address is in the URL of a request',
  ).toBe(false);
});

test('an address that already has an account gets the same answer and a mail that says so, with no link', async ({ page }) => {
  test.setTimeout(240_000);
  // The development user has an account on every clean stack, whatever the other tests did.
  const email = setting('E2E_SEED_EMAIL');

  await page.goto('/signup');
  await page.getByLabel('Email').fill(email);
  await page.getByLabel('Company name').fill(`Other ${unique('co')}`);
  await page.getByRole('button', { name: 'Create account' }).click();
  await expect(page.getByTestId('signup-sent')).toHaveText(SENT); // the sentence a new address gets

  const mail = await waitForAccountExistsMail(email, MAIL_WAIT_MS);
  expect(mail.text.includes('already exists'), 'the mail does not say that the account exists').toBe(true);
  for (const part of [mail.text, mail.html]) {
    expect(part.includes('token='), 'the mail holds a token').toBe(false);
    expect(part.includes('://'), 'the mail holds a link').toBe(false);
  }
});

test('an account made between the request and the link: the completion screen says it exists and offers the sign-in', async ({ page }) => {
  test.setTimeout(300_000);
  // The script invited this address through the operator CLI; the invitation mail comes at the server's next poll.
  const email = setting('E2E_BOTH_EMAIL');
  const password = setting('E2E_USER_PASSWORD');

  await page.goto('/signup');
  await page.getByLabel('Email').fill(email);
  await page.getByLabel('Company name').fill(`Both ${unique('co')}`);
  await page.getByRole('button', { name: 'Create account' }).click();
  await expect(page.getByTestId('signup-sent')).toHaveText(SENT);
  const link = await waitForLink(email, 'complete-signup', MAIL_WAIT_MS);

  // The invitation is accepted by the API: the account exists now, and the sign-up link is still unused.
  await acceptInviteByApi(await waitForLink(email, 'invite', MAIL_WAIT_MS), password);

  await openMailLink(page, link);
  await page.getByLabel('New password').fill(password);
  await page.getByLabel('Repeat the password').fill(password);
  await page.getByRole('button', { name: 'Create company' }).click();
  await expect(page.getByText('An account with this address already exists.')).toBeVisible();
  await expect(page.getByLabel('New password')).toHaveCount(0); // no form: there is nothing left to finish
  await page.getByRole('link', { name: 'Sign in.' }).click();
  await expect(page).toHaveURL(/\/login$/);
});

test('a link that expired says so and offers to sign up again', async ({ page }) => {
  // The script asked for this sign-up and made the end of its link lie in the past (the database, not the clock of the test).
  const link = await waitForLink(setting('E2E_EXPIRED_EMAIL'), 'complete-signup', 10_000);

  await openMailLink(page, link);
  await expect(page.getByText(LINK_USED_UP)).toBeVisible();
  await expect(page).toHaveURL(/\/complete-signup$/);
  expectCleanAddressBar(page);

  await page.getByRole('link', { name: 'Sign up again' }).click();
  await expect(page).toHaveURL(/\/signup$/);
});

test('a link that never worked says so and offers to sign up again', async ({ page }) => {
  await page.goto(`/complete-signup?token=${'x'.repeat(43)}`);
  await expect(page.getByText(LINK_USED_UP)).toBeVisible();
  await expect(page).toHaveURL(/\/complete-signup$/);
  expectCleanAddressBar(page);

  await page.getByRole('link', { name: 'Sign up again' }).click();
  await expect(page).toHaveURL(/\/signup$/);
});

test('a person whose company is gone comes back by sign-up: only the company name is asked for, and the old password still works', async ({ page }) => {
  test.setTimeout(240_000);
  // The script gave this address an account (an invitation to the development company, accepted with E2E_USER_PASSWORD) and then removed it
  // from the company through the operator CLI: an account, and no company. The test asks for the sign-up itself.
  const email = setting('E2E_COMEBACK_EMAIL');
  const password = setting('E2E_USER_PASSWORD');
  const company = `Back ${unique('co')}`;

  await page.goto('/signup');
  await page.getByLabel('Email').fill(email);
  await page.getByLabel('Company name').fill(`Asked ${unique('co')}`);
  await page.getByRole('button', { name: 'Create account' }).click();
  await expect(page.getByTestId('signup-sent')).toHaveText(SENT); // the sentence every address gets

  // The mail carries the usual link (not the notice), and the screen it opens asks for the company name only.
  const link = await waitForLink(email, 'complete-signup', MAIL_WAIT_MS);
  await openMailLink(page, link);
  await expect(page).toHaveURL(/\/complete-signup$/);
  expectCleanAddressBar(page);
  await expect(page.getByLabel('Email')).toHaveValue(email);
  await expect(page.getByLabel('Email')).not.toBeEditable();
  await expect(page.getByTestId('keeps-password')).toBeVisible();
  await expect(page.getByLabel('New password')).toHaveCount(0);
  await expect(page.getByLabel('Repeat the password')).toHaveCount(0);

  await page.getByLabel('Company name').fill(company);
  await page.getByRole('button', { name: 'Create company' }).click();

  // Signed in at once, as the first admin of a company of their own; nothing of the former company.
  await expect(page).toHaveURL(/\/notes$/);
  expectNoEmailInAddressBar(page);
  await expect(page.getByTestId('me-email')).toHaveText(email);
  await expect(page.getByTestId('me-company')).toHaveText(company);
  await expect(page.getByTestId('me-role')).toHaveText('admin');
  await expect(page.getByText(setting('E2E_SEEDED_NOTE'))).toHaveCount(0);
  await expect(page.getByText('No notes yet.')).toBeVisible();

  // The password did not change: signing out and in again with the one the account always had lands in the new company.
  await signOut(page);
  await signIn(page, email, password);
  await expect(page.getByTestId('me-company')).toHaveText(company);
  await expect(page.getByTestId('me-role')).toHaveText('admin');
});
