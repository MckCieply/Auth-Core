import { expect, test } from '@playwright/test';
import { setting } from './support/env';
import { waitForLink } from './support/mail';
import { expectCleanAddressBar, expectNoEmailInAddressBar } from './support/page';
import { submitLogin } from './support/session';

test('the viewer the operator invited accepts from the mail, signs in, reads the notes and cannot add one', async ({ page }) => {
  test.setTimeout(240_000);
  const email = setting('E2E_VIEWER_EMAIL');
  const password = setting('E2E_USER_PASSWORD');

  // The script asked the operator CLI to invite this address as a viewer; the server sends the mail within a minute or two.
  await page.goto(await waitForLink(email, 'invite', 100_000));
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
