import { expect, test } from '@playwright/test';
import { inviteByApi, inviteFromNewCompanyByApi } from './support/accounts';
import { acceptInviteByApi } from './support/api';
import { setting } from './support/env';
import { openMailLink, waitForLink } from './support/mail';
import { expectCleanAddressBar, expectNoEmailInAddressBar } from './support/page';
import { signIn, submitLogin } from './support/session';

test('the viewer the operator invited accepts from the mail, signs in, reads the notes and cannot add one', async ({ page }) => {
  test.setTimeout(240_000);
  const email = setting('E2E_VIEWER_EMAIL');
  const password = setting('E2E_USER_PASSWORD');

  // The script asked the operator CLI to invite this address as a viewer; the server sends the mail within a minute or two.
  await openMailLink(page, await waitForLink(email, 'invite', 100_000));
  await expect(page.getByTestId('invite-join')).toHaveText(/^\s*Join .+ as viewer$/);
  await expect(page.getByLabel('Email')).toHaveValue(email);
  await expect(page.getByLabel('Email')).not.toBeEditable();
  await expect(page.getByText(`This sets the password for ${email}.`)).toBeVisible();
  expectCleanAddressBar(page);

  await page.getByLabel('New password').fill(password);
  await page.getByLabel('Repeat the password').fill(password);
  await page.getByRole('button', { name: 'Join' }).click();

  // On the sign-in the email is filled in, and it was not put in the address bar.
  await expect(page).toHaveURL(/\/login$/);
  await expect(page.getByLabel('Email')).toHaveValue(email);
  expectNoEmailInAddressBar(page);

  await submitLogin(page, email, password);
  await expect(page).toHaveURL(/\/notes$/);
  await expect(page.getByTestId('me-email')).toHaveText(email);
  await expect(page.getByTestId('me-role')).toHaveText('viewer');
  await expect(page.getByText(setting('E2E_SEEDED_NOTE'))).toBeVisible();
  await expect(page.getByLabel('New note')).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Add note' })).toHaveCount(0);
});

function unique(prefix: string): string {
  return `${prefix}-${Date.now()}-${Math.floor(Math.random() * 1_000_000)}`;
}

/** The link of the newest invitation mail that is not `old`: the first invitation of the same address stays the newest until the second mail arrives. */
async function waitForNewInviteLink(email: string, old: string, timeoutMs: number): Promise<string> {
  const deadline = Date.now() + timeoutMs;
  for (;;) {
    const link = await waitForLink(email, 'invite', Math.max(1000, deadline - Date.now()));
    if (link !== old) {
      return link;
    }
    if (Date.now() > deadline) {
      throw new Error(`no second invitation mail for ${email} within ${timeoutMs / 1000} seconds`);
    }
    await new Promise((resolve) => setTimeout(resolve, 1000));
  }
}

test('a member of the development company, signed in, accepts an invitation of another company with the button and lands in its notes', async ({ page }) => {
  test.setTimeout(300_000);
  const email = `${unique('joiner')}@e2e.test`;
  const password = setting('E2E_USER_PASSWORD');
  const mailWaitMs = 100_000;

  // A member of the development company: invited and accepted by the API, then signed in on the sample.
  await inviteByApi(email, 'user');
  const first = await waitForLink(email, 'invite', mailWaitMs);
  await acceptInviteByApi(first, password);
  await signIn(page, email, password);
  const startedIn = ((await page.getByTestId('me-company').textContent()) ?? '').trim();
  expect(startedIn, 'the new member is in no company').not.toBe('');

  // A second company invites the same address; the company is deleted whatever happens.
  const other = await inviteFromNewCompanyByApi(email, 'user');
  let failure: unknown;
  try {
    await openMailLink(page, await waitForNewInviteLink(email, first, mailWaitMs));
    await expect(page).toHaveURL(/\/invite$/);
    expectCleanAddressBar(page);
    // The address has an account: one line and one button, no password field.
    await expect(page.getByTestId('invite-existing')).toBeVisible();
    await expect(page.getByTestId('invite-existing')).toContainText(other.companyName);
    await expect(page.getByLabel('New password')).toHaveCount(0);
    await page.getByRole('button', { name: 'Accept the invitation' }).click();

    // The signed-in person is the invited one: the page switches into the company and opens its notes.
    await expect(page).toHaveURL(/\/notes$/);
    await expect(page.getByTestId('me-email')).toHaveText(email);
    await expect(page.getByTestId('me-company')).toHaveText(other.companyName);
    // The picker of spec 0015 lists both companies (not the "New company..." entry).
    const options = page.getByTestId('org-picker').locator('option:not([value="new-company"])');
    await expect(options).toHaveCount(2);
    await expect(options.filter({ hasText: startedIn })).toHaveCount(1);
    await expect(options.filter({ hasText: other.companyName })).toHaveCount(1);
  } catch (error) {
    failure = error;
  }
  try {
    await other.remove();
  } catch (error) {
    failure ??= error;
  }
  if (failure !== undefined) {
    throw failure;
  }
});
