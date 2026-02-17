import { expect, test } from '@playwright/test';
import { setting } from './support/env';
import { signOut, submitLogin } from './support/session';

const CSP =
  /^default-src 'self'; script-src 'self'; style-src 'self' 'nonce-([0-9a-f-]{36})'; connect-src 'self'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'self'; form-action 'self'$/;

test('the app answers with the Content-Security-Policy and the other headers of the spec', async ({ page }) => {
  const response = await page.goto('/login');
  if (response === null) {
    throw new Error('no response for /login');
  }
  const headers = response.headers();
  expect(headers['content-security-policy']).toMatch(CSP);
  expect(headers['content-security-policy']).not.toContain('unsafe-inline');
  expect(headers['referrer-policy']).toBe('no-referrer');
  expect(headers['x-content-type-options']).toBe('nosniff');
  expect(headers['cache-control']).toBe('no-cache');
});

test('the nonce is new for every response and is the one in the page', async ({ page }) => {
  const nonceOf = (value: string | undefined) => CSP.exec(value ?? '')?.[1];
  const first = await page.goto('/login');
  const firstNonce = nonceOf(first?.headers()['content-security-policy']);
  expect(firstNonce).toBeDefined();
  await expect(page.locator('app-root')).toHaveAttribute('ngcspnonce', firstNonce as string);
  const second = await page.goto('/forgot');
  const secondNonce = nonceOf(second?.headers()['content-security-policy']);
  expect(secondNonce).toBeDefined();
  expect(secondNonce).not.toBe(firstNonce);
  await expect(page.locator('app-root')).toHaveAttribute('ngcspnonce', secondNonce as string);
});

test('every path that is index.html is revalidated, an unknown path and a missing .js file included', async ({ page }) => {
  for (const path of ['/', '/notes', '/no/such/page', '/missing.js', '/missing.css', '/index.html']) {
    const answer = await page.request.get(path);
    expect(answer.status(), path).toBe(200);
    expect(answer.headers()['cache-control'], path).toBe('no-cache');
    expect(await answer.text(), path).toContain('<app-root');
  }
});

test('the hashed .js and .css files of the build are not marked no-cache: they may be cached', async ({ page }) => {
  const index = await (await page.request.get('/')).text();
  const named = [/<script[^>]* src="([^"]+\.js)"/.exec(index)?.[1], /<link[^>]* href="([^"]+\.css)"/.exec(index)?.[1]];
  expect(named.length).toBe(2);
  for (const file of named) {
    expect(file, 'index.html names no .js or .css file').toBeDefined();
    const answer = await page.request.get(`/${(file as string).replace(/^\//, '')}`);
    expect(answer.status(), file).toBe(200);
    expect(answer.headers()['content-type'], file).toMatch(/javascript|css/);
    expect(answer.headers()['cache-control'] ?? '', file).not.toContain('no-cache');
    expect(answer.headers()['content-security-policy'], file).toBeDefined();
  }
});

test('the file server serves nothing outside the build', async ({ page }) => {
  for (const path of ['/Caddyfile', '/etc/passwd', '/.env', '/Dockerfile']) {
    const answer = await page.request.get(path);
    const text = await answer.text();
    expect(text, path).toContain('<app-root');
    expect(text, path).not.toContain('reverse_proxy');
    expect(text, path).not.toContain('root:');
  }
});

test('the headers of the app are not put on /auth and /api, which set their own', async ({ page }) => {
  for (const path of ['/auth/health', '/api/health']) {
    const answer = await page.request.get(path);
    expect(answer.status(), path).toBe(200);
    expect(answer.headers()['content-security-policy'], path).toBeUndefined();
    expect(answer.headers()['referrer-policy'], path).toBeUndefined();
  }
});

test('the style nonce works: every style element carries it, nothing is blocked, and the page has no inline script', async ({ page }) => {
  const violations: string[] = [];
  page.on('console', (message) => {
    if (/content security policy/i.test(message.text())) {
      violations.push(message.text());
    }
  });
  await page.addInitScript(() => {
    const seen: string[] = [];
    (window as unknown as { __csp: string[] }).__csp = seen;
    document.addEventListener('securitypolicyviolation', (event) => {
      seen.push(`${event.violatedDirective} ${event.blockedURI}`);
    });
  });
  const response = await page.goto('/login');
  const nonce = CSP.exec(response?.headers()['content-security-policy'] ?? '')?.[1];
  expect(nonce).toBeDefined();

  // Sign in and out in this same document (signIn() would load /login again, with a new nonce): the screens, their styles
  // and their calls all run under the policy of this response.
  await submitLogin(page, setting('E2E_SEED_EMAIL'), setting('E2E_SEED_PASSWORD'));
  await expect(page).toHaveURL(/\/notes$/);
  await expect(page.getByTestId('me-email')).toHaveText(setting('E2E_SEED_EMAIL'));
  await signOut(page);

  const styles = await page.evaluate(() =>
    Array.from(document.querySelectorAll('style'), (style) => style.nonce || style.getAttribute('nonce')),
  );
  expect(styles.length).toBeGreaterThan(0);
  expect(new Set(styles)).toEqual(new Set([nonce]));
  expect(await page.evaluate(() => document.querySelectorAll('script:not([src])').length)).toBe(0);
  expect(await page.evaluate(() => (window as unknown as { __csp: string[] }).__csp)).toEqual([]);
  expect(violations).toEqual([]);
});

test.describe('a mail link leaves no token in the address bar once its screen is open', () => {
  const token = 'not-a-real-token-0123456789abcdefghijklmnopqrs';

  test('/reset', async ({ page }) => {
    await page.goto(`/reset?token=${token}`);
    await expect(page.getByRole('heading', { name: 'Choose a new password' })).toBeVisible();
    expect(new URL(page.url()).search).toBe('');
    expect(page.url()).not.toContain(token);
  });

  test('/verify', async ({ page }) => {
    await page.goto(`/verify?token=${token}`);
    await expect(page.getByText('This link has expired or was already used.')).toBeVisible();
    expect(new URL(page.url()).search).toBe('');
    expect(page.url()).not.toContain(token);
  });

  test('/invite', async ({ page }) => {
    await page.goto(`/invite?token=${token}`);
    await expect(page.getByText('This invitation has expired or was already used. Ask for a new one.')).toBeVisible();
    await expect(page.getByRole('link', { name: 'Ask for a new link' })).toHaveCount(0);
    expect(new URL(page.url()).search).toBe('');
    expect(page.url()).not.toContain(token);
  });
});
