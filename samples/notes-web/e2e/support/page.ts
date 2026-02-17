import { Page, expect } from '@playwright/test';

/**
 * The address bar holds no query and no email. The assertions are booleans with fixed messages: a failure must not print the
 * address, which could still hold a token or an email.
 */
export function expectCleanAddressBar(page: Page): void {
  const url = new URL(page.url());
  expect(url.search === '', 'the address bar still holds a query').toBe(true);
}

export function expectNoEmailInAddressBar(page: Page): void {
  const url = page.url();
  expect(url.includes('@') || url.includes('%40'), 'the address bar holds an email address').toBe(false);
}

/** Collects the URLs of the requests the page makes to /auth/refresh from now on. */
export function watchRefreshes(page: Page): string[] {
  const refreshes: string[] = [];
  page.on('request', (request) => {
    if (request.url().endsWith('/auth/refresh')) {
      refreshes.push(request.url());
    }
  });
  return refreshes;
}
