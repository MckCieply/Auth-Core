import { Page, expect, test } from '@playwright/test';
import { NEW_COMPANY, createCompany, deleteCompanyByApi, picker, signUpPrivatelyByApi } from './support/accounts';
import { setting } from './support/env';
import { signIn } from './support/session';
import { openSettings } from './support/settings';

// Spec 0016, creating another company from the notes screen. The picker ends with "New company..." (three ASCII dots) when the options say
// org_creation self-service, which the stack of scripts/e2e-web.sh turns on. Both tests delete the company they made through the API, and the
// seed user of the first ends in the company where it started (as 12-orgs.spec.ts does).
const MAIL_WAIT_MS = 100_000;

function unique(prefix: string): string {
  return `${prefix}-${Date.now()}-${Math.floor(Math.random() * 1_000_000)}`;
}

test('"New company..." creates a company and shows it as current with no notes', async ({ page }) => {
  test.setTimeout(120_000);
  const email = setting('E2E_SEED_EMAIL');
  const password = setting('E2E_SEED_PASSWORD');
  const name = `Gamma ${Date.now()}`;

  await signIn(page, email, password);
  const startedIn = ((await page.getByTestId('me-company').textContent()) ?? '').trim();
  expect(startedIn, 'the seed user is in no company').not.toBe('');

  let failure: unknown;
  try {
    // The picker offers the entry as its last option.
    // It waits: the entry is rendered once the options answer has arrived.
    await expect(picker(page).locator('option').last()).toHaveText(NEW_COMPANY);

    await createCompany(page, name);

    // The new company is current: the bar names it, the creator is its admin, the picker has it chosen, and no note of another company shows.
    await expect(page.getByTestId('me-role')).toHaveText('admin');
    await expect(picker(page).locator('option:checked')).toHaveText(name);
    await expect(page.getByText('No notes yet.')).toBeVisible();
    await expect(page.getByText(setting('E2E_SEEDED_NOTE'))).toHaveCount(0);
  } catch (error) {
    failure = error;
  }

  // The clean-up runs whatever happened: the company goes by the API, and a new sign-in puts the seed user back where it started.
  try {
    await deleteCompanyByApi(email, password, name);
    await page.context().clearCookies();
    await signIn(page, email, password);
    if (((await page.getByTestId('me-company').textContent()) ?? '').trim() !== startedIn) {
      await picker(page).selectOption({ label: startedIn });
    }
    await expect(page.getByTestId('me-company')).toHaveText(startedIn);
    await expect(picker(page).locator('option', { hasText: name })).toHaveCount(0);
  } catch (error) {
    failure ??= error;
  }
  if (failure !== undefined) {
    throw failure;
  }
});

test('a private person who created a company, back in Private, sees the company_member text and keeps the notes', async ({ page }) => {
  test.setTimeout(300_000);
  const email = `${unique('creator')}@e2e.test`;
  const password = setting('E2E_USER_PASSWORD');
  const name = `Solo ${unique('c')}`;
  const note = `Private note ${unique('n')}`;

  await signUpPrivatelyByApi(email, password, MAIL_WAIT_MS);
  await signIn(page, email, password);
  await expect(page.getByTestId('me-private')).toHaveText('Private account');

  let failure: unknown;
  let companyDeleted = false;
  try {
    // A private person is offered "New company..." too, and the new company is current at once.
    await createCompany(page, name);
    await expect(page.getByTestId('me-private')).toHaveCount(0);

    // Back in Private: a note, then the settings show the company_member text with the company's name and no button, and the note is still there.
    await picker(page).selectOption({ label: 'Private' });
    await expect(page.getByTestId('me-private')).toHaveText('Private account');
    await expect(picker(page)).toBeEnabled();
    await page.getByLabel('New note').fill(note);
    await page.getByRole('button', { name: 'Add note' }).click();
    await expect(page.getByText(note)).toBeVisible();

    await openSettings(page);
    await expect(page.getByTestId('delete-company-member')).toContainText(`You are still a member of ${name}.`);
    await expect(page.getByTestId('delete-account')).toHaveCount(0);
    await page.goto('/notes');
    await expect(page.getByText(note)).toBeVisible();

    // The company is removed by the API for the set-up (with the company's own token); the settings then offer the button, and the form succeeds.
    expect(await deleteCompanyByApi(email, password, name), 'the company was not found to delete').toBe(true);
    companyDeleted = true;
    await page.reload();
    await openSettings(page);
    await page.getByTestId('delete-account').click();
    await page.locator('#delete-password').fill(password);
    await page.getByTestId('delete-confirm').click();
    await expect(page).toHaveURL(/\/login$/);
    await expect(page.getByTestId('login-notice')).toHaveText('Your account was deleted.');
  } catch (error) {
    failure = error;
  }

  // The clean-up: a company left behind by a failed run goes by the API, so the stack stays as it was found (the unique address is not reused).
  if (!companyDeleted) {
    try {
      await deleteCompanyByApi(email, password, name);
    } catch (error) {
      failure ??= error;
    }
  }
  if (failure !== undefined) {
    throw failure;
  }
});
