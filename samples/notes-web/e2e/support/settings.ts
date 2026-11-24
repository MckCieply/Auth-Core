import { Page, expect } from '@playwright/test';

/** Opens the settings screen by the header's link, as a person does. */
export async function openSettings(page: Page): Promise<void> {
  await page.getByTestId('settings-link').click();
  await expect(page).toHaveURL(/\/settings$/);
  await expect(page.getByRole('heading', { name: 'Settings', level: 1 })).toBeVisible();
}

/**
 * Spec 0012 → Testing endpoint: ends the confirmation window of the browser's session. The browser's own cookie jar (`page.request`) holds
 * the refresh cookie: a refresh gives an access token of that session (and the next cookie, which the app then uses), and the testing
 * endpoint ends the window with it. The server's clock is not moved. Nothing of it is printed.
 */
export async function endConfirmationWindow(page: Page): Promise<void> {
  const refreshed = await page.request.post('/auth/refresh');
  expect(refreshed.status(), 'the refresh before ending the window').toBe(200);
  const body = (await refreshed.json()) as { access_token?: unknown };
  expect(typeof body.access_token, 'the refresh gave no access token').toBe('string');
  const ended = await page.request.post('/auth/testing/end-confirmation', {
    headers: { Authorization: `Bearer ${String(body.access_token)}` },
  });
  expect(ended.status(), 'the testing endpoint (is Auth__Testing__WindowEndpoint on?)').toBe(204);
}
