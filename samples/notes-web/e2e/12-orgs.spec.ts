import { Page, expect, test } from '@playwright/test';
import { setting } from './support/env';
import { watchRefreshes } from './support/page';
import { signIn, signOut } from './support/session';

// Spec 0015, the company picker. The stack of scripts/e2e-web.sh seeds a second company for the development user: AUTH_DEV_SEED_SECOND_ORG_NAME,
// "Beta" in .env.example. The user is an admin of the first company and a plain member of Beta.
//
// The last company a person used is the one the next sign-in lands in. scripts/e2e-web.sh starts a clean stack for each project, but a kept
// stack (E2E_KEEP_STACK=1) or a hand-run `npx playwright test` reuses one, and the other specs expect the first company (its admin and its
// seeded note). So the flow here ends with the seed user back in the first company, and the last test checks that it did.
const SECOND = 'Beta';

const seed = () => ({ email: setting('E2E_SEED_EMAIL'), password: setting('E2E_SEED_PASSWORD') });

const picker = (page: Page) => page.getByTestId('org-picker');

/** The company options of the picker, without the "New company..." entry (value new-company) that spec 0016 adds. */
const companyOptions = (page: Page) => picker(page).locator('option:not([value="new-company"])');

/** The names the picker offers, in the order of the list. */
async function pickerNames(page: Page): Promise<string[]> {
  return companyOptions(page).allTextContents();
}

/** Chooses a company in the picker and waits until the account bar names it and the picker is usable again. */
async function chooseCompany(page: Page, name: string): Promise<void> {
  await picker(page).selectOption({ label: name });
  await expect(page.getByTestId('me-company')).toHaveText(name);
  await expect(picker(page).locator('option:checked')).toHaveText(name);
  await expect(picker(page)).toBeEnabled();
}

async function addNote(page: Page, text: string): Promise<void> {
  await page.getByLabel('New note').fill(text);
  await page.getByRole('button', { name: 'Add note' }).click();
  await expect(page.getByText(text)).toBeVisible();
}

/**
 * Puts the seed user back in the first company, from wherever a failed test left the page: on the sign-in screen (it signs in, which lands
 * in the company used last), or on the notes screen in the second company.
 */
async function backToFirstCompany(page: Page, first: string): Promise<void> {
  const { email, password } = seed();
  if (new URL(page.url()).pathname !== '/notes') {
    await signIn(page, email, password);
  }
  if (((await page.getByTestId('me-company').textContent()) ?? '').trim() !== first) {
    await chooseCompany(page, first);
  }
  await expect(page.getByTestId('me-company')).toHaveText(first);
}

test('the picker lists both companies, and a switch moves the notes and the session, also across a reload and a new sign-in', async ({ page }) => {
  const { email, password } = seed();
  const refreshes = watchRefreshes(page);
  const firstNote = `a note of the first company ${Date.now()}`;
  const secondNote = `a note of ${SECOND} ${Date.now()}`;

  await signIn(page, email, password);
  const company = page.getByTestId('me-company');
  // The first company is the one of the picker that is not Beta, whichever of the two the seed user was left in.
  await expect(companyOptions(page)).toHaveCount(2);
  const first = (await pickerNames(page)).map((name) => name.trim()).find((name) => name !== SECOND);
  expect(first, `the picker offers no company besides ${SECOND}`).toBeDefined();
  await backToFirstCompany(page, first!);

  let failure: unknown;
  try {
    // The picker lists the first company and Beta, with the first one chosen.
    await expect(picker(page)).toBeVisible();
    await expect(page.getByLabel('Company', { exact: true })).toBeVisible();
    await expect(companyOptions(page)).toHaveCount(2);
    expect((await pickerNames(page)).map((name) => name.trim()).sort()).toEqual([first!, SECOND].sort());
    await expect(picker(page).locator('option:checked')).toHaveText(first!);

    // A note of the first company.
    await expect(page.getByText(setting('E2E_SEEDED_NOTE'))).toBeVisible();
    await addNote(page, firstNote);

    // Beta: the bar names it, and nothing of the first company is listed.
    await chooseCompany(page, SECOND);
    await expect(page.getByText(firstNote)).toHaveCount(0);
    await expect(page.getByText(setting('E2E_SEEDED_NOTE'))).toHaveCount(0);

    // A note of Beta, listed at once.
    await addNote(page, secondNote);

    // A reload: the refresh gives the session back in Beta, with its note and not the other one.
    const before = refreshes.length;
    await page.reload();
    await expect(page).toHaveURL(/\/notes$/);
    await expect(page.getByTestId('me-email')).toHaveText(email);
    await expect(company).toHaveText(SECOND);
    await expect(page.getByText(secondNote)).toBeVisible();
    await expect(page.getByText(firstNote)).toHaveCount(0);
    await expect(picker(page).locator('option:checked')).toHaveText(SECOND);
    expect(refreshes.length, 'the reload did not ask for a session with /auth/refresh').toBeGreaterThan(before);

    // Sign out and in: the sign-in lands in the company used last.
    await signOut(page);
    await signIn(page, email, password);
    await expect(company).toHaveText(SECOND);
    await expect(page.getByText(secondNote)).toBeVisible();
    await expect(page.getByText(firstNote)).toHaveCount(0);

    // And back: the first company has its notes again.
    await chooseCompany(page, first!);
    await expect(page.getByText(firstNote)).toBeVisible();
    await expect(page.getByText(setting('E2E_SEEDED_NOTE'))).toBeVisible();
    await expect(page.getByText(secondNote)).toHaveCount(0);
  } catch (error) {
    failure = error;
  }
  // The restore runs whatever happened; its own failure shows only when the test had none, so it never hides the first one.
  try {
    await backToFirstCompany(page, first!);
  } catch (error) {
    failure ??= error;
  }
  if (failure !== undefined) {
    throw failure;
  }
});

test('switching back leaves the seed user where it started', async ({ page }) => {
  const { email, password } = seed();
  await signIn(page, email, password);
  // The first company: the one the other specs expect, with its admin role and the note the script seeded.
  await expect(page.getByTestId('me-company')).not.toHaveText(SECOND);
  await expect(page.getByTestId('me-role')).toHaveText('admin');
  await expect(page.getByText(setting('E2E_SEEDED_NOTE'))).toBeVisible();
  await expect(picker(page).locator('option:checked')).not.toHaveText(SECOND);

  await signOut(page);
  await signIn(page, email, password);
  await expect(page.getByTestId('me-company')).not.toHaveText(SECOND);
  await expect(page.getByTestId('me-role')).toHaveText('admin');
});
