import { Page, expect, test } from '@playwright/test';
import { setting } from './support/env';
import { openLogin, signIn, signOut, submitLogin } from './support/session';

const seed = () => ({ email: setting('E2E_SEED_EMAIL'), password: setting('E2E_SEED_PASSWORD') });

async function expectTokenNowhere(page: Page, token: string): Promise<void> {
  const places = await page.evaluate(() =>
    JSON.stringify({
      local: { ...localStorage },
      session: { ...sessionStorage },
      cookie: document.cookie,
      url: location.href,
    }),
  );
  // Booleans, not the text: a failing assertion would print the text, and the text holds the token.
  expect(places.includes(token), 'the access token is in storage, a cookie or the URL').toBe(false);
  // The refresh cookie is HttpOnly: script cannot read it either.
  expect(places.includes('auth_rt'), 'the refresh cookie is readable by script').toBe(false);
}

test('a reload lands on the page that was open, signed in, with no sign-in screen in between', async ({ page }) => {
  const { email, password } = seed();
  await signIn(page, email, password);
  const visited: string[] = [];
  page.on('framenavigated', (frame) => {
    if (frame === page.mainFrame()) {
      visited.push(new URL(frame.url()).pathname);
    }
  });
  await page.reload();
  await expect(page.getByTestId('me-email')).toHaveText(email);
  await expect(page).toHaveURL(/\/notes$/);
  expect(visited).not.toContain('/login');
});

test('after sign-out a reload shows the sign-in, and the notes ask for it again', async ({ page }) => {
  const { email, password } = seed();
  await signIn(page, email, password);
  await signOut(page);
  await page.reload();
  await expect(page).toHaveURL(/\/login$/);
  await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible();
  await page.goto('/notes');
  await expect(page).toHaveURL(/\/login\?returnUrl=%2Fnotes$/);
});

test('the access token is nowhere but in memory: not in storage, a cookie or the URL, after sign-in and after a refresh', async ({ page }) => {
  const { email, password } = seed();
  await openLogin(page);
  const loginAnswer = page.waitForResponse((r) => r.url().endsWith('/auth/login') && r.request().method() === 'POST');
  await submitLogin(page, email, password);
  const first = ((await (await loginAnswer).json()) as { access_token: string }).access_token;
  await expect(page).toHaveURL(/\/notes$/);
  await expect(page.getByTestId('me-email')).toHaveText(email);
  await expectTokenNowhere(page, first);

  const refreshAnswer = page.waitForResponse((r) => r.url().endsWith('/auth/refresh') && r.request().method() === 'POST');
  await page.reload();
  const second = ((await (await refreshAnswer).json()) as { access_token: string }).access_token;
  await expect(page.getByTestId('me-email')).toHaveText(email);
  await expectTokenNowhere(page, second);
  await expectTokenNowhere(page, first);
});

test('the refresh cookie is HttpOnly, Secure, SameSite=Strict and for /auth only', async ({ page }) => {
  const { email, password } = seed();
  await openLogin(page);
  const loginAnswer = page.waitForResponse((r) => new URL(r.url()).pathname === '/auth/login' && r.request().method() === 'POST');
  await submitLogin(page, email, password);
  const setCookies = ((await (await loginAnswer).allHeaders())['set-cookie'] ?? '').split('\n');
  await expect(page).toHaveURL(/\/notes$/);

  // What Auth-Core sets: the attributes of the Set-Cookie line, never the whole line (a failing assertion would print its value).
  // SameSite is read here and not from context.cookies(): Playwright's WebKit reports "None" for a Strict cookie (seen with 1.58.2).
  const line = setCookies.find((c) => c.startsWith('auth_rt='));
  expect(line, 'the sign-in sets no refresh cookie').toBeDefined();
  const attributes = line!.split(';').slice(1).map((a) => a.trim().toLowerCase());
  expect(attributes).toEqual(expect.arrayContaining(['httponly', 'secure', 'samesite=strict', 'path=/auth']));

  // What the browser holds: the same, apart from SameSite.
  const refresh = (await page.context().cookies()).find((c) => c.name === 'auth_rt');
  expect(refresh).toBeDefined();
  const { httpOnly, secure, path } = refresh!;
  expect({ httpOnly, secure, path }).toEqual({ httpOnly: true, secure: true, path: '/auth' });
});

test('signing out ends the session for good: the cookie is gone and the notes are not reachable by the old page', async ({ page }) => {
  const { email, password } = seed();
  await signIn(page, email, password);
  await signOut(page);
  const cookies = await page.context().cookies();
  expect(cookies.find((c) => c.name === 'auth_rt' && c.value !== '')).toBeUndefined();
  await page.goBack();
  // Going back to the notes screen asks for the session again and finds none.
  await expect(page).toHaveURL(/\/login\?returnUrl=%2Fnotes$/);
});
