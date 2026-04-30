import { Page, expect } from '@playwright/test';

declare const process: { env: Record<string, string | undefined> };

export type FakeProvider = 'Google' | 'Microsoft';

/** The fake OpenID provider of spec 0011 (tests/Auth.FakeOidc/compose.yml): its sign-in page is where the browser lands. */
export function fakeUrl(): string {
  return process.env['E2E_FAKE_URL'] ?? 'http://localhost:8099';
}

/**
 * The browser is on the fake's sign-in page. The redirects from Auth-Core's start take a moment, so this waits for the address (a
 * function, so that a failure prints no address) and then for the heading.
 */
async function atFake(page: Page, provider: FakeProvider): Promise<void> {
  const origin = `${fakeUrl()}/`;
  await page.waitForURL((url) => url.href.startsWith(origin));
  await expect(page.getByRole('heading', { name: `Sign in to the fake ${provider}` })).toBeVisible();
}

/** Signs in at the fake as the person of this address; `verified` ticks "The provider verified this address". */
export async function signInAtFake(page: Page, provider: FakeProvider, email: string, verified: boolean): Promise<void> {
  await atFake(page, provider);
  await page.getByLabel('Email').fill(email);
  if (verified) {
    await page.getByLabel('The provider verified this address').check();
  }
  await page.getByRole('button', { name: 'Sign in' }).click();
}

/** Chooses one of the fake's fixed people by the text of its link. */
export async function chooseAtFake(page: Page, provider: FakeProvider, person: string): Promise<void> {
  await atFake(page, provider);
  await page.getByRole('link', { name: person }).click();
}

export async function cancelAtFake(page: Page, provider: FakeProvider): Promise<void> {
  await atFake(page, provider);
  await page.getByRole('link', { name: 'Cancel' }).click();
}

/** Back in the app with no ticket in the address bar: no query and no fragment. Fixed messages: the URL is never printed. */
export function expectNoTicketInAddressBar(page: Page): void {
  const url = new URL(page.url());
  expect(url.search === '' && url.hash === '', 'the address bar still holds a query or a fragment').toBe(true);
  expect(page.url().includes('ticket'), 'the address bar holds a ticket').toBe(false);
}
