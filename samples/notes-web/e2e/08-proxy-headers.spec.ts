import { expect, test } from '@playwright/test';
import { apiUrl } from './support/env';

const AUTH_CORE_POLICY = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

test('the app answers with the headers the proxy adds', async ({ page }) => {
  const answer = await page.request.get('/login');
  expect(answer.status()).toBe(200);
  const headers = answer.headers();
  expect(headers['x-frame-options']).toBe('DENY');
  expect(headers['cross-origin-opener-policy']).toBe('same-origin');
  expect(headers['permissions-policy']).toBe('camera=(), microphone=(), geolocation=(), payment=()');
  expect(headers['content-security-policy']).toContain("frame-ancestors 'none'");   // the policy of spec 0007 is still there
});

test('HSTS is sent over HTTPS, and not over plain HTTP', async ({ page }) => {
  const secure = await page.request.get('/login');
  expect(secure.headers()['strict-transport-security']).toBe('max-age=31536000');
  const plain = await page.request.get(`${apiUrl()}/login`);
  expect(plain.status()).toBe(200);
  expect(plain.headers()['strict-transport-security']).toBeUndefined();
});

test('the notes service gets nosniff and X-Frame-Options from the proxy, over HTTPS and over plain HTTP', async ({ page }) => {
  for (const base of ['', apiUrl()]) {
    const answer = await page.request.get(`${base}/api/health`);
    expect(answer.status(), base).toBe(200);
    expect(answer.headers()['x-content-type-options'], base).toBe('nosniff');
    expect(answer.headers()['x-frame-options'], base).toBe('DENY');
  }
});

test("Auth-Core's own headers reach the browser through the proxy, with HSTS added over HTTPS", async ({ page }) => {
  const answer = await page.request.get('/auth/health');
  expect(answer.status()).toBe(200);
  const headers = answer.headers();
  expect(headers['content-security-policy']).toBe(AUTH_CORE_POLICY);
  expect(headers['x-content-type-options']).toBe('nosniff');
  expect(headers['x-frame-options']).toBe('DENY');
  expect(headers['cross-origin-resource-policy']).toBe('same-origin');
  expect(headers['referrer-policy']).toBe('no-referrer');
  expect(headers['cache-control']).toBe('no-store');
  expect(headers['strict-transport-security']).toBe('max-age=31536000');
});
