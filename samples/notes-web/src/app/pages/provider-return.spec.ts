import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component, inject } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { vi } from 'vitest';
import { has, pageText, providerMe, realLocation, settle } from '../../testing/helpers';
import { rememberProvider } from './provider-buttons';
import { ProviderReturnPage } from './provider-return';

const status = (code: number) => ({ status: code, statusText: String(code) });
const TICKET = 'TICKET-abcdefghijklmnopqrstuvwxyz-0123456789A';
const GENERIC = 'Google or Microsoft could not sign you in. Try again.';

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

describe('ProviderReturnPage (spec 0011)', () => {
  afterEach(() => {
    history.replaceState(null, '', '/');
    sessionStorage.clear();
    localStorage.clear();
  });

  it('removes the fragment from the address bar and the router before it calls Auth-Core', async () => {
    const { ctrl, router } = await open(`ticket=${TICKET}&purpose=sign_in`);
    expect(window.location.hash).toBe('');
    expect(window.location.href).not.toContain(TICKET);
    expect(router.url).toBe('/provider-return');
    const request = ctrl.expectOne('/auth/providers/exchange');
    expect(request.request.body).toEqual({ ticket: TICKET });
    expect(stored()).not.toContain(TICKET);
  });

  it('the address bar holds no ticket at the moment the request runs, and the history gets no new entry', async () => {
    const { ctrl, entries } = await open(`ticket=${TICKET}&purpose=sign_in`);
    expect(addressAtRequest).toHaveLength(1);
    expect(addressAtRequest[0]).not.toContain(TICKET);
    expect(addressAtRequest[0]).not.toContain('#');
    expect(addressAtRequest[0].endsWith('/provider-return')).toBe(true);
    expect(history.length).toBe(entries);
    ctrl.expectOne('/auth/providers/exchange'); // the request carries the ticket that was read before the address changed
  });

  it('signed in: the notes open, replacing this screen', async () => {
    const { harness, ctrl, router, entries } = await open(`ticket=${TICKET}&purpose=sign_in`);
    ctrl.expectOne('/auth/providers/exchange').flush({ status: 'authenticated', access_token: 'tok-p' });
    await settle();
    ctrl.expectOne('/auth/me').flush(providerMe);
    await settle(harness.fixture);
    await settle(harness.fixture);
    expect(router.url).toBe('/notes');
    expect(window.location.pathname).toBe('/notes');
    expect(history.length).toBe(entries);
  });

  it.each([['false' as const], ['rejects' as const]])(
    'the fragment cannot be removed (%s): nothing is sent or handed on, the provider_error sentence and the way back are shown',
    async (removal) => {
      for (const purpose of ['sign_in', 'invite', 'reauth']) {
        TestBed.resetTestingModule();
        const opened = await open(`ticket=${TICKET}&purpose=${purpose}`, removal);
        opened.ctrl.expectNone('/auth/providers/exchange');
        expect(addressAtRequest).toEqual([]);
        expect(Landing.opened).toBe(0);
        expect(Landing.state).toBeUndefined();
        expect(opened.router.url).toContain('#');
        expect(pageText(opened.harness.fixture)).toContain(GENERIC);
        expect(pageText(opened.harness.fixture)).not.toContain(TICKET);
        expect(has(opened.harness.fixture, 'a[href="/login"]')).toBe(true);
        expect(has(opened.harness.fixture, '[data-testid="try-again"]')).toBe(false);
      }
    },
  );

  it.each([['false' as const], ['rejects' as const]])('the fragment cannot be removed (%s) and an error fragment: the provider_error sentence, not the error in the address', async (removal) => {
    const opened = await open('error=cancelled&purpose=sign_in', removal);
    expect(pageText(opened.harness.fixture)).toContain(GENERIC);
    expect(pageText(opened.harness.fixture)).not.toContain('Sign-in was cancelled.');
  });

  it('no fragment and a refused navigation (nothing to remove): still /login', async () => {
    const opened = await open(null, 'false');
    opened.ctrl.expectNone('/auth/providers/exchange');
    expect(opened.router.url).toBe('/login');
  });

  it('an invitation round trip is exchanged too', async () => {
    const { ctrl } = await open(`ticket=${TICKET}&purpose=invite`);
    expect(ctrl.expectOne('/auth/providers/exchange').request.body).toEqual({ ticket: TICKET });
  });

  it('next link: /provider-link with the ticket, the address, whether it is verified and the provider, in the navigation state only', async () => {
    rememberProvider('microsoft');
    const opened = await open(`ticket=${TICKET}&purpose=sign_in`);
    opened.ctrl.expectOne('/auth/providers/exchange').flush({ next: 'link', email: 'anna@firma.example', email_verified: true });
    await settled(opened);
    expect(opened.router.url).toBe('/provider-link');
    expect(Landing.state).toMatchObject({ ticket: TICKET, email: 'anna@firma.example', emailVerified: true, provider: 'microsoft' });
    expect(opened.router.url).not.toContain(TICKET);
    expect(window.location.href).not.toContain(TICKET);
    expect(stored()).not.toContain(TICKET);
    expect(history.length).toBe(opened.entries);
  });

  it('next link without a remembered provider: the state says null', async () => {
    const opened = await open(`ticket=${TICKET}&purpose=sign_in`);
    opened.ctrl.expectOne('/auth/providers/exchange').flush({ next: 'link', email: 'anna@firma.example', email_verified: false });
    await settled(opened);
    expect(Landing.state).toEqual({ ticket: TICKET, email: 'anna@firma.example', emailVerified: false, provider: null });
  });

  it('next finish_signup: /provider-finish with the ticket, the address and the kinds', async () => {
    const opened = await open(`ticket=${TICKET}&purpose=sign_in`);
    opened.ctrl.expectOne('/auth/providers/exchange').flush({ next: 'finish_signup', email: 'new@gmail.example', kinds: ['organization', 'private'] });
    await settled(opened);
    expect(opened.router.url).toBe('/provider-finish');
    expect(Landing.state).toEqual({ ticket: TICKET, email: 'new@gmail.example', kinds: ['organization', 'private'] });
    expect(window.location.href).not.toContain(TICKET);
    expect(stored()).not.toContain(TICKET);
    expect(history.length).toBe(opened.entries);
  });

  it('reauth: the notes screen with the ticket in the navigation state, and no call', async () => {
    const opened = await open(`ticket=${TICKET}&purpose=reauth`);
    opened.ctrl.expectNone('/auth/providers/exchange');
    expect(opened.router.url).toBe('/notes');
    expect(Landing.state).toEqual({ reauthTicket: TICKET });
    expect(window.location.href).not.toContain(TICKET);
    expect(stored()).not.toContain(TICKET);
    expect(addressAtRequest).toEqual([]);
    expect(history.length).toBe(opened.entries);
  });

  it.each([null, 'purpose=sign_in', `ticket=${TICKET}&purpose=other`, `ticket=${TICKET}`, 'ticket=&purpose=sign_in', `ticket=${TICKET}&purpose=SIGN_IN`, `ticket=${TICKET}&purpose=`])(
    '%s: /login, no call, and the address and storage are clean',
    async (fragment) => {
      const opened = await open(fragment);
      opened.ctrl.expectNone('/auth/providers/exchange');
      expect(opened.router.url).toBe('/login');
      expect(Landing.state).toBeUndefined();
      expectNoTicketAnywhere(opened);
      expect(history.length).toBe(opened.entries);
    },
  );

  it.each([
    ['cancelled', 'Sign-in was cancelled.'],
    ['invalid_state', 'The sign-in took too long or was started in another browser. Try again.'],
    ['provider_unavailable', 'This way to sign in is not available.'],
    ['provider_error', GENERIC],
    ['something_new', GENERIC],
    ['constructor', GENERIC],
    ['__proto__', GENERIC],
    ['toString', GENERIC],
    ['hasOwnProperty', GENERIC],
    ['CANCELLED', GENERIC],
    ['', GENERIC],
  ])('error=%s says "%s" with a link back to sign in', async (code, text) => {
    const opened = await open(`error=${code}&purpose=sign_in`);
    opened.ctrl.expectNone('/auth/providers/exchange');
    expect(pageText(opened.harness.fixture)).toContain(text);
    expect(has(opened.harness.fixture, 'a[href="/login"]')).toBe(true);
    expect(window.location.hash).toBe('');
    expect(opened.router.url).toBe('/provider-return');
  });

  it.each(['sign_in', 'invite', 'reauth', 'unknown', ''])('an error with the purpose "%s" shows its message and calls nobody', async (purpose) => {
    const opened = await open(`error=cancelled&purpose=${purpose}`);
    opened.ctrl.expectNone('/auth/providers/exchange');
    expect(pageText(opened.harness.fixture)).toContain('Sign-in was cancelled.');
    expect(opened.router.url).toBe('/provider-return');
  });

  it('an error without a purpose still shows its message', async () => {
    const opened = await open('error=invalid_state');
    expect(pageText(opened.harness.fixture)).toContain('The sign-in took too long');
  });

  it('an error and a ticket together: the message, and the ticket is not exchanged', async () => {
    const opened = await open(`error=cancelled&ticket=${TICKET}&purpose=sign_in`);
    opened.ctrl.expectNone('/auth/providers/exchange');
    expect(pageText(opened.harness.fixture)).toContain('Sign-in was cancelled.');
    expectNoTicketAnywhere(opened);
  });

  it('extra parameters are ignored: the request holds the ticket and nothing else', async () => {
    const opened = await open(`ticket=${TICKET}&purpose=sign_in&state=s-1&code=c-1&extra=%3Cb%3E&email=evil%40x.example`);
    const request = opened.ctrl.expectOne('/auth/providers/exchange');
    expect(request.request.body).toEqual({ ticket: TICKET });
    expect(request.request.url).toBe('/auth/providers/exchange');
    expect(window.location.hash).toBe('');
    expect(window.location.href).not.toContain('state=s-1');
  });

  it('a repeated parameter reads as its first value, the others are dropped from the address too', async () => {
    const opened = await open(`ticket=${TICKET}&ticket=OTHER-TICKET&purpose=sign_in&purpose=reauth`);
    expect(opened.ctrl.expectOne('/auth/providers/exchange').request.body).toEqual({ ticket: TICKET });
    expect(window.location.href).not.toContain('OTHER-TICKET');
    expect(window.location.hash).toBe('');
  });

  it('a repeated error reads as its first value', async () => {
    const opened = await open('error=cancelled&error=provider_error&purpose=sign_in');
    expect(pageText(opened.harness.fixture)).toContain('Sign-in was cancelled.');
  });

  it('parameters in the query, not the fragment, count for nothing', async () => {
    Landing.state = undefined;
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: 'provider-return', component: ProviderReturnPage },
          { path: 'login', component: Landing },
        ]),
        provideHttpClient(),
        provideHttpClientTesting(),
        realLocation,
      ],
    });
    const url = `/provider-return?ticket=${TICKET}&purpose=sign_in`;
    history.replaceState(null, '', url);
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(url);
    await settle(harness.fixture);
    await settle(harness.fixture);
    TestBed.inject(HttpTestingController).expectNone('/auth/providers/exchange');
    expect(TestBed.inject(Router).url).toBe('/login');
  });

  it.each([
    [400, 'invalid_ticket', 'This sign-in took too long. Start again.'],
    [403, 'no_account', 'You have no account. Ask your company for an invitation.'],
    [403, 'no_email', 'Your Microsoft or Google account has no email address we can use. Sign in with your password.'],
    [409, 'already_registered', 'An account with this address exists. Sign in with your password.'],
    [403, 'address_mismatch', 'Use the Google or Microsoft account of the invited address, or open the invitation link again and choose a password.'],
    [409, 'already_member', 'This account already belongs to a company.'],
    [409, 'private_account_exists', 'You have a private account with this address. Open the invitation link again to move it into the company.'],
    [409, 'provider_already_linked', 'This Google or Microsoft account already signs in to another account.'],
    [400, 'invalid_token', 'This invitation has expired or was already used. Ask for a new one.'],
    [403, 'no_membership', 'Your account does not belong to a company.'],
    [400, 'invalid_request', 'Something went wrong. Try again.'],
    [500, 'oops', 'Something went wrong. Try again.'],
  ])('the exchange answers %s %s: "%s"', async (code, error, text) => {
    const opened = await open(`ticket=${TICKET}&purpose=invite`);
    opened.ctrl.expectOne('/auth/providers/exchange').flush({ error }, status(code));
    await settle(opened.harness.fixture);
    expect(pageText(opened.harness.fixture)).toContain(text);
    expect(has(opened.harness.fixture, 'a[href="/login"]')).toBe(true);
    expect(opened.router.url).toBe('/provider-return');
    expect(Landing.opened).toBe(0);
    expectNoTicketAnywhere(opened);
  });

  it.each([
    ['an HTML body', '<html>', 200],
    ['a next the sample does not know', { next: 'other' }, 200],
    ['a link answer without the verified flag', { next: 'link', email: 'a@b.example' }, 200],
    ['a finish answer without a known kind', { next: 'finish_signup', email: 'a@b.example', kinds: ['x'] }, 200],
    ['an empty answer', null, 200],
  ])('%s: "Something went wrong" and the way back, no navigation', async (_name, body, code) => {
    const opened = await open(`ticket=${TICKET}&purpose=sign_in`);
    opened.ctrl.expectOne('/auth/providers/exchange').flush(body, status(code));
    await settle(opened.harness.fixture);
    expect(pageText(opened.harness.fixture)).toContain('Something went wrong. Try again.');
    expect(has(opened.harness.fixture, 'a[href="/login"]')).toBe(true);
    expect(Landing.opened).toBe(0);
  });

  it('a 429 keeps the ticket in memory and offers "Try again", which asks again with it', async () => {
    const { harness, ctrl } = await open(`ticket=${TICKET}&purpose=sign_in`);
    ctrl.expectOne('/auth/providers/exchange').flush({ error: 'too_many_requests', retry_after_seconds: 3 }, status(429));
    await settle(harness.fixture);
    (harness.fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('[data-testid="try-again"]')!.click();
    expect(ctrl.expectOne('/auth/providers/exchange').request.body).toEqual({ ticket: TICKET });
  });

  it('a 429: no message of failure, the address stays clean, and the second answer is followed', async () => {
    const opened = await open(`ticket=${TICKET}&purpose=sign_in`);
    opened.ctrl.expectOne('/auth/providers/exchange').flush({ error: 'too_many_requests', retry_after_seconds: 3 }, status(429));
    await settle(opened.harness.fixture);
    expect(pageText(opened.harness.fixture)).not.toContain('Something went wrong');
    expect(has(opened.harness.fixture, '[data-testid="provider-message"]')).toBe(false);
    expect(has(opened.harness.fixture, '[data-testid="try-again"]')).toBe(true);
    expectNoTicketAnywhere(opened);
    (opened.harness.fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('[data-testid="try-again"]')!.click();
    opened.harness.fixture.detectChanges();
    expect(has(opened.harness.fixture, '[data-testid="try-again"]')).toBe(false);
    opened.ctrl.expectOne('/auth/providers/exchange').flush({ next: 'link', email: 'anna@firma.example', email_verified: true });
    await settled(opened);
    expect(opened.router.url).toBe('/provider-link');
    expect(Landing.state).toMatchObject({ ticket: TICKET });
    expect(addressAtRequest).toHaveLength(2);
    expect(addressAtRequest.join(' ')).not.toContain(TICKET);
  });

  it('two quick clicks on "Try again" send one request', async () => {
    const opened = await open(`ticket=${TICKET}&purpose=sign_in`);
    opened.ctrl.expectOne('/auth/providers/exchange').flush({ error: 'too_many_requests', retry_after_seconds: 3 }, status(429));
    await settle(opened.harness.fixture);
    const button = (opened.harness.fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('[data-testid="try-again"]')!;
    button.click();
    button.click();
    expect(opened.ctrl.match('/auth/providers/exchange')).toHaveLength(1);
  });

  it('a second 429 offers "Try again" again, and a refusal after it shows its message', async () => {
    const opened = await open(`ticket=${TICKET}&purpose=sign_in`);
    opened.ctrl.expectOne('/auth/providers/exchange').flush({ error: 'too_many_requests', retry_after_seconds: 3 }, status(429));
    await settle(opened.harness.fixture);
    const again = () => (opened.harness.fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('[data-testid="try-again"]')!.click();
    again();
    opened.ctrl.expectOne('/auth/providers/exchange').flush({ error: 'too_many_requests', retry_after_seconds: 3 }, status(429));
    await settle(opened.harness.fixture);
    expect(has(opened.harness.fixture, '[data-testid="try-again"]')).toBe(true);
    again();
    opened.ctrl.expectOne('/auth/providers/exchange').flush({ error: 'invalid_ticket' }, status(400));
    await settle(opened.harness.fixture);
    expect(pageText(opened.harness.fixture)).toContain('This sign-in took too long. Start again.');
    expect(has(opened.harness.fixture, '[data-testid="try-again"]')).toBe(false);
  });

  it('the ticket is shown nowhere while the request runs', async () => {
    const opened = await open(`ticket=${TICKET}&purpose=sign_in`);
    expect(pageText(opened.harness.fixture)).toContain('Signing you in...');
    expectNoTicketAnywhere(opened);
    opened.ctrl.expectOne('/auth/providers/exchange');
  });
});
