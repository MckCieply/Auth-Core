import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component, inject } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { vi } from 'vitest';
import { clickOn, has, pageText, providerMe, realLocation, settle } from '../../testing/helpers';
import { armReauth, rememberPendingConnect, rememberPendingDisconnect, rememberProvider } from './provider-buttons';
import { ProviderReturnPage } from './provider-return';

const status = (code: number) => ({ status: code, statusText: String(code) });
const TICKET = 'TICKET-abcdefghijklmnopqrstuvwxyz-0123456789A';

/** The screens the return screen sends the person to; each keeps the navigation state it was opened with. */
@Component({ template: '<p>landed</p>' })
class Landing {
  static state: Record<string, unknown> | undefined;
  static opened = 0;
  constructor() {
    Landing.opened += 1;
    Landing.state = inject(Router).currentNavigation()?.extras.state;
  }
}

/** What the address bar said at the moment each request left the browser. */
const addressAtRequest: string[] = [];

async function open(fragment: string | null, removal: 'works' | 'false' | 'rejects' = 'works') {
  Landing.state = undefined;
  Landing.opened = 0;
  addressAtRequest.length = 0;
  TestBed.configureTestingModule({
    providers: [
      provideRouter([
        { path: 'provider-return', component: ProviderReturnPage },
        { path: 'notes', component: Landing },
        { path: 'login', component: Landing },
        { path: 'provider-link', component: Landing },
        { path: 'provider-finish', component: Landing },
        { path: 'settings', component: Landing },
        { path: 'team', component: Landing },
      ]),
      provideHttpClient(
        withInterceptors([
          (request, next) => {
            addressAtRequest.push(window.location.href);
            return next(request);
          },
        ]),
      ),
      provideHttpClientTesting(),
      realLocation,
    ],
  });
  const url = fragment === null ? '/provider-return' : `/provider-return#${fragment}`;
  history.replaceState(null, '', url);
  if (removal !== 'works') {
    // The navigation that takes the fragment out of the address is refused (false) or fails (rejects).
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate');
    if (removal === 'false') {
      navigate.mockResolvedValue(false);
    } else {
      navigate.mockRejectedValue(new Error('the address cannot be changed'));
    }
  }
  const harness = await RouterTestingHarness.create();
  await harness.navigateByUrl(url);
  // The entry of this screen is there; from now on the screen's own navigations must replace it, never add one.
  const entries = history.length;
  await settle(harness.fixture);
  await settle(harness.fixture); // the navigation that removes the fragment lands first
  return { harness, ctrl: TestBed.inject(HttpTestingController), router: TestBed.inject(Router), entries };
}

type Opened = Awaited<ReturnType<typeof open>>;

function stored(): string {
  return JSON.stringify({ ...sessionStorage }) + JSON.stringify({ ...localStorage });
}

async function settled(opened: Opened): Promise<void> {
  await settle(opened.harness.fixture);
  await settle(opened.harness.fixture);
}

/** The ticket is in no address, no storage and nowhere on the page. */
function expectNoTicketAnywhere(opened: Opened): void {
  expect(window.location.href).not.toContain(TICKET);
  expect(stored()).not.toContain(TICKET);
  expect(pageText(opened.harness.fixture)).not.toContain(TICKET);
  expect(addressAtRequest.join(' ')).not.toContain(TICKET);
}

describe('ProviderReturnPage: the targets of the settings screen (spec 0012)', () => {
  afterEach(() => {
    history.replaceState(null, '', '/');
    sessionStorage.clear();
    localStorage.clear();
  });

  it('connect with the connect flag: POST /auth/providers/connect with the ticket, then /settings saying it is connected', async () => {
    rememberProvider('github');
    armReauth('connect');
    const opened = await open(`ticket=${TICKET}&purpose=connect`);
    const request = opened.ctrl.expectOne('/auth/providers/connect');
    expect(request.request.body).toEqual({ ticket: TICKET });
    request.flush(null, status(204));
    await settled(opened);
    opened.ctrl.expectOne('/auth/me').flush(providerMe);
    await settled(opened);
    expect(opened.router.url).toBe('/settings');
    expect(Landing.state).toEqual({ notice: 'GitHub connected.' });
    expectNoTicketAnywhere(opened);
    expect(history.length).toBe(opened.entries);
  });

  it.each([null, 'confirm', 'delete_account', 'delete_company'] as const)('connect with the flag %s: /login, no call', async (target) => {
    if (target !== null) {
      armReauth(target);
    }
    const opened = await open(`ticket=${TICKET}&purpose=connect`);
    opened.ctrl.expectNone('/auth/providers/connect');
    opened.ctrl.expectNone('/auth/account/confirm');
    expect(opened.router.url).toBe('/login');
    expect(Landing.state).toBeUndefined();
    expectNoTicketAnywhere(opened);
  });

  it.each(['connect', null] as const)('reauth with the flag %s: /login, no call', async (target) => {
    if (target !== null) {
      armReauth(target);
    }
    const opened = await open(`ticket=${TICKET}&purpose=reauth`);
    opened.ctrl.expectNone('/auth/account/confirm');
    opened.ctrl.expectNone('/auth/providers/connect');
    expect(opened.router.url).toBe('/login');
    expect(Landing.state).toBeUndefined();
    expectNoTicketAnywhere(opened);
  });

  it('a flag that does not fit is used up: the next return finds none', async () => {
    armReauth('confirm');
    const first = await open(`ticket=${TICKET}&purpose=connect`);
    first.ctrl.expectNone('/auth/providers/connect');
    expect(first.router.url).toBe('/login');
    TestBed.resetTestingModule();
    const second = await open(`ticket=${TICKET}&purpose=reauth`);
    second.ctrl.expectNone('/auth/account/confirm');
    expect(second.router.url).toBe('/login');
  });

  it('a connect refused for a closed window: /settings with the ticket and the provider in the navigation state only', async () => {
    rememberProvider('google');
    armReauth('connect');
    const opened = await open(`ticket=${TICKET}&purpose=connect`);
    opened.ctrl.expectOne('/auth/providers/connect').flush({ error: 'confirmation_required' }, status(403));
    await settled(opened);
    expect(opened.router.url).toBe('/settings');
    expect(Landing.state).toEqual({ connectTicket: TICKET, provider: 'google' });
    expect(window.location.href).not.toContain(TICKET);
    expect(stored()).not.toContain(TICKET);
    expect(addressAtRequest.join(' ')).not.toContain(TICKET);
    expect(history.length).toBe(opened.entries);
  });

  it.each([
    [409, 'provider_in_use', 'You already have a Google account connected. Disconnect it first.'],
    [409, 'provider_already_linked', 'This Google account belongs to another account.'],
    [403, 'email_not_verified', 'Google has not verified this address.'],
    [403, 'no_email', 'This Google account has no email address we can use.'],
    [400, 'invalid_ticket', 'This took too long. Start again.'],
    [403, 'no_membership', 'Your account does not belong to a company.'],
    [500, 'boom', 'Something went wrong. Try again.'],
  ])('a connect refused with %i %s: /settings with its sentence', async (code, error, sentence) => {
    rememberProvider('google');
    armReauth('connect');
    const opened = await open(`ticket=${TICKET}&purpose=connect`);
    opened.ctrl.expectOne('/auth/providers/connect').flush({ error }, status(code));
    await settled(opened);
    expect(opened.router.url).toBe('/settings');
    expect(Landing.state).toEqual({ notice: sentence });
    expectNoTicketAnywhere(opened);
  });

  it('a connect without a remembered provider names "Google, Microsoft or GitHub"', async () => {
    armReauth('connect');
    const opened = await open(`ticket=${TICKET}&purpose=connect`);
    opened.ctrl.expectOne('/auth/providers/connect').flush(null, status(204));
    await settled(opened);
    opened.ctrl.expectOne('/auth/me').flush(providerMe);
    await settled(opened);
    expect(Landing.state).toEqual({ notice: 'Google, Microsoft or GitHub connected.' });
  });

  it("address_mismatch names the account's address, read from GET /auth/me", async () => {
    rememberProvider('github');
    armReauth('connect');
    const opened = await open(`ticket=${TICKET}&purpose=connect`);
    opened.ctrl.expectOne('/auth/providers/connect').flush({ error: 'address_mismatch' }, status(403));
    await settled(opened);
    opened.ctrl.expectOne('/auth/me').flush(providerMe);
    await settled(opened);
    expect((Landing.state?.['notice'] as string | undefined) ?? '').toContain(providerMe.email);
    expect(Landing.state).toEqual({ notice: `This GitHub account has another address. Connect the one with ${providerMe.email}.` });
  });

  it('a connect answered 401 session_required: nothing is handed on and nobody is sent anywhere (the interceptor signs the person out)', async () => {
    rememberProvider('github');
    armReauth('connect');
    const opened = await open(`ticket=${TICKET}&purpose=connect`);
    opened.ctrl.expectOne('/auth/providers/connect').flush({ error: 'session_required' }, status(401));
    await settled(opened);
    expect(opened.router.url).toBe('/provider-return');
    expect(Landing.opened).toBe(0);
    expectNoTicketAnywhere(opened);
  });

  it('reauth with the confirm flag: POST /auth/account/confirm with the ticket, then /settings', async () => {
    armReauth('confirm');
    const opened = await open(`ticket=${TICKET}&purpose=reauth`);
    const request = opened.ctrl.expectOne('/auth/account/confirm');
    expect(request.request.body).toEqual({ provider_ticket: TICKET });
    request.flush(null, status(204));
    await settled(opened);
    opened.ctrl.expectOne('/auth/me').flush(providerMe);
    await settled(opened);
    expect(opened.router.url).toBe('/settings');
    expect(Landing.state).toBeUndefined();
    expectNoTicketAnywhere(opened);
    expect(history.length).toBe(opened.entries);
  });

  it('reauth for a pending connect: /settings says "Confirmed. Connect GitHub again."', async () => {
    rememberPendingConnect('github');
    armReauth('confirm');
    const opened = await open(`ticket=${TICKET}&purpose=reauth`);
    opened.ctrl.expectOne('/auth/account/confirm').flush(null, status(204));
    await settled(opened);
    opened.ctrl.expectOne('/auth/me').flush(providerMe);
    await settled(opened);
    expect(Landing.state).toEqual({ notice: 'Confirmed. Connect GitHub again.' });
    expect(sessionStorage.getItem('notes-web.pending-connect')).toBeNull();
  });

  it('reauth for a pending disconnect: /settings says "Confirmed. Disconnect Google again.", once', async () => {
    rememberPendingDisconnect('google');
    armReauth('confirm');
    const opened = await open(`ticket=${TICKET}&purpose=reauth`);
    opened.ctrl.expectOne('/auth/account/confirm').flush(null, status(204));
    await settled(opened);
    opened.ctrl.expectOne('/auth/me').flush(providerMe);
    await settled(opened);
    expect(Landing.state).toEqual({ notice: 'Confirmed. Disconnect Google again.' });
    expect(sessionStorage.getItem('notes-web.pending-connect')).toBeNull();
  });

  it('a pending disconnect is used up by a refused confirmation too, and says nothing of it', async () => {
    rememberPendingDisconnect('google');
    armReauth('confirm');
    const opened = await open(`ticket=${TICKET}&purpose=reauth`);
    opened.ctrl.expectOne('/auth/account/confirm').flush({ error: 'reauth_failed' }, status(403));
    await settled(opened);
    expect(Landing.state).toEqual({ notice: 'The confirmation did not work or took too long. Confirm again.' });
    expect(sessionStorage.getItem('notes-web.pending-connect')).toBeNull();
  });

  it('a pending connect is used up by a refused confirmation too', async () => {
    rememberPendingConnect('github');
    armReauth('confirm');
    const opened = await open(`ticket=${TICKET}&purpose=reauth`);
    opened.ctrl.expectOne('/auth/account/confirm').flush({ error: 'reauth_failed' }, status(403));
    await settled(opened);
    expect(Landing.state).toEqual({ notice: 'The confirmation did not work or took too long. Confirm again.' });
    expect(sessionStorage.getItem('notes-web.pending-connect')).toBeNull();
  });

  it.each([
    [403, 'reauth_failed', 'The confirmation did not work or took too long. Confirm again.'],
    [400, 'invalid_ticket', 'This took too long. Start again.'],
    [403, 'no_membership', 'Your account does not belong to a company.'],
    [500, 'boom', 'Something went wrong. Try again.'],
  ])('a confirmation refused with %i %s: /settings with its sentence', async (code, error, sentence) => {
    armReauth('confirm');
    const opened = await open(`ticket=${TICKET}&purpose=reauth`);
    opened.ctrl.expectOne('/auth/account/confirm').flush({ error }, status(code));
    await settled(opened);
    expect(opened.router.url).toBe('/settings');
    expect(Landing.state).toEqual({ notice: sentence });
    expectNoTicketAnywhere(opened);
  });

  it('a confirmation answered 401 session_required: nobody is sent anywhere', async () => {
    armReauth('confirm');
    const opened = await open(`ticket=${TICKET}&purpose=reauth`);
    opened.ctrl.expectOne('/auth/account/confirm').flush({ error: 'session_required' }, status(401));
    await settled(opened);
    expect(opened.router.url).toBe('/provider-return');
    expect(Landing.opened).toBe(0);
  });

  it('a connect rate-limited: the ticket is kept, "Try again" repeats the connect with it', async () => {
    rememberProvider('github');
    armReauth('connect');
    const opened = await open(`ticket=${TICKET}&purpose=connect`);
    opened.ctrl.expectOne('/auth/providers/connect').flush({ error: 'too_many_requests', retry_after_seconds: 5 }, status(429));
    await settled(opened);
    expect(opened.router.url).toBe('/provider-return');
    expect(Landing.opened).toBe(0);
    expect(has(opened.harness.fixture, '[data-testid="provider-message"]')).toBe(false);
    expectNoTicketAnywhere(opened);
    clickOn(opened.harness.fixture, '[data-testid="try-again"]');
    await settled(opened);
    const again = opened.ctrl.expectOne('/auth/providers/connect');
    expect(again.request.body).toEqual({ ticket: TICKET });
    again.flush(null, status(204));
    await settled(opened);
    opened.ctrl.expectOne('/auth/me').flush(providerMe);
    await settled(opened);
    expect(Landing.state).toEqual({ notice: 'GitHub connected.' });
  });

  it('a confirmation rate-limited: the ticket and the pending connect are kept, "Try again" repeats the confirmation', async () => {
    rememberPendingConnect('github');
    armReauth('confirm');
    const opened = await open(`ticket=${TICKET}&purpose=reauth`);
    opened.ctrl.expectOne('/auth/account/confirm').flush({ error: 'too_many_requests', retry_after_seconds: 5 }, status(429));
    await settled(opened);
    expect(opened.router.url).toBe('/provider-return');
    expect(Landing.opened).toBe(0);
    expect(sessionStorage.getItem('notes-web.pending-connect')).toMatch(/^connect:github:\d+$/);
    expectNoTicketAnywhere(opened);
    clickOn(opened.harness.fixture, '[data-testid="try-again"]');
    await settled(opened);
    const again = opened.ctrl.expectOne('/auth/account/confirm');
    expect(again.request.body).toEqual({ provider_ticket: TICKET });
    again.flush(null, status(204));
    await settled(opened);
    opened.ctrl.expectOne('/auth/me').flush(providerMe);
    await settled(opened);
    expect(Landing.state).toEqual({ notice: 'Confirmed. Connect GitHub again.' });
  });

  it('an error fragment of a connect this tab started: /settings with its sentence, and the pending connect is used up', async () => {
    rememberPendingConnect('github');
    armReauth('connect');
    const opened = await open('purpose=connect&error=cancelled');
    expect(opened.router.url).toBe('/settings');
    expect(Landing.state).toEqual({ notice: 'Sign-in was cancelled.' });
    expect(sessionStorage.getItem('notes-web.pending-connect')).toBeNull();
    expect(history.length).toBe(opened.entries);
  });

  it('an error fragment of a confirmation this tab started: /settings with its sentence, and the pending connect is used up', async () => {
    rememberPendingConnect('github');
    armReauth('confirm');
    const opened = await open('purpose=reauth&error=cancelled');
    expect(opened.router.url).toBe('/settings');
    expect(Landing.state).toEqual({ notice: 'Sign-in was cancelled.' });
    expect(sessionStorage.getItem('notes-web.pending-connect')).toBeNull();
  });

  it.each([
    ['purpose=connect&error=cancelled', null],
    ['purpose=reauth&error=cancelled', 'connect'],
    ['purpose=connect&error=cancelled', 'confirm'],
    ['purpose=sign_in&error=cancelled', 'connect'],
  ] as const)('an error fragment %s with the flag %s stays on this screen', async (fragment, target) => {
    if (target !== null) {
      armReauth(target);
    }
    const opened = await open(fragment);
    expect(opened.router.url).toBe('/provider-return');
    expect(Landing.opened).toBe(0);
    expect(pageText(opened.harness.fixture)).toContain('Sign-in was cancelled.');
  });

  it('reauth with the delete_company flag: /team with the ticket in the navigation state, and no call', async () => {
    armReauth('delete_company');
    const opened = await open(`ticket=${TICKET}&purpose=reauth`);
    opened.ctrl.expectNone('/auth/account/confirm');
    expect(opened.router.url).toBe('/team');
    expect(Landing.state).toEqual({ reauthTicket: TICKET });
    expect(window.location.href).not.toContain(TICKET);
    expect(stored()).not.toContain(TICKET);
  });

  it('reauth with the delete_account flag still goes to the notes screen with the ticket, and no call', async () => {
    armReauth('delete_account');
    const opened = await open(`ticket=${TICKET}&purpose=reauth`);
    opened.ctrl.expectNone('/auth/account/confirm');
    expect(opened.router.url).toBe('/notes');
    expect(Landing.state).toEqual({ reauthTicket: TICKET });
  });
});
