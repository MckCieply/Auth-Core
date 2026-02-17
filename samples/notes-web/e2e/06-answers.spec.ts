import { expect, test } from '@playwright/test';
import { setting } from './support/env';
import { watchRefreshes } from './support/page';
import { openLogin, submitLogin } from './support/session';

const seed = () => ({ email: setting('E2E_SEED_EMAIL'), password: setting('E2E_SEED_PASSWORD') });

test('a 401 once on /api/notes leads to one refresh and the list', async ({ page }) => {
  const { email, password } = seed();
  let listCalls = 0;
  await page.route('**/api/notes', async (route) => {
    if (route.request().method() !== 'GET') {
      await route.continue();
      return;
    }
    listCalls += 1;
    if (listCalls === 1) {
      await route.fulfill({ status: 401, headers: { 'WWW-Authenticate': 'Bearer' }, body: '' });
    } else {
      await route.continue();
    }
  });
  await openLogin(page);
  const refreshes = watchRefreshes(page);
  await submitLogin(page, email, password);
  await expect(page.getByText(setting('E2E_SEEDED_NOTE'))).toBeVisible();
  expect(listCalls).toBe(2);
  expect(refreshes).toHaveLength(1);
  await expect(page.getByTestId('notice')).toHaveCount(0);
});

test('a 401 from the refresh leads to /login?returnUrl=%2Fnotes', async ({ page }) => {
  const { email, password } = seed();
  await page.route('**/api/notes', (route) => route.fulfill({ status: 401, headers: { 'WWW-Authenticate': 'Bearer' }, body: '' }));
  await page.route('**/auth/refresh', (route) =>
    route.fulfill({ status: 401, contentType: 'application/json', body: JSON.stringify({ error: 'invalid_grant' }) }),
  );
  await openLogin(page);
  await submitLogin(page, email, password);
  await expect(page).toHaveURL(/\/login\?returnUrl=%2Fnotes$/);
  await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible();
});

test('a 503 auth_unavailable shows the bar and keeps the header', async ({ page }) => {
  const { email, password } = seed();
  await page.route('**/api/notes', (route) =>
    route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ error: 'auth_unavailable' }) }),
  );
  await openLogin(page);
  await submitLogin(page, email, password);
  await expect(page.getByTestId('notice-text')).toHaveText('Try again shortly.');
  await expect(page.getByTestId('me-email')).toHaveText(email);
  await expect(page).toHaveURL(/\/notes$/);
});

test('a 403 forbidden shows the message and does not refresh', async ({ page }) => {
  const { email, password } = seed();
  await page.route('**/api/notes', (route) =>
    route.fulfill({ status: 403, contentType: 'application/json', body: JSON.stringify({ error: 'forbidden' }) }),
  );
  await openLogin(page);
  const refreshes = watchRefreshes(page);
  await submitLogin(page, email, password);
  await expect(page.getByTestId('notice-text')).toHaveText("You don't have access to this.");
  await expect(page.getByTestId('me-email')).toHaveText(email);
  expect(refreshes).toHaveLength(0);
});
