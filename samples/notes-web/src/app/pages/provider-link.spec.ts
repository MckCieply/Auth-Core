import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { clickOn, has, pageText, privateMe, realLocation, settle, submitForm, typeInto } from '../../testing/helpers';
import { AuthService } from '../auth/auth.service';
import { ProviderLinkPage } from './provider-link';

const status = (code: number) => ({ status: code, statusText: String(code) });

@Component({ template: '<p>elsewhere</p>' })
class Elsewhere {}

async function open(state: Record<string, unknown> | null) {
  TestBed.configureTestingModule({
    providers: [
      provideRouter([
        { path: 'start', component: Elsewhere },
        { path: 'provider-link', component: ProviderLinkPage },
        { path: 'login', component: Elsewhere },
        { path: 'forgot', component: Elsewhere },
        { path: 'signup', component: Elsewhere },
        { path: 'notes', component: Elsewhere },
      ]),
      provideHttpClient(),
      provideHttpClientTesting(),
      realLocation,
    ],
  });
  history.replaceState(null, '', '/start');
  const harness = await RouterTestingHarness.create('/start');
  const router = TestBed.inject(Router);
  await harness.navigateByUrl('/start');
  const entries = history.length;
  await router.navigateByUrl('/provider-link', state === null ? {} : { state });
  await settle(harness.fixture);
  await settle(harness.fixture); // the navigation that takes the ticket out of the history entry lands here
  return { harness, router, ctrl: TestBed.inject(HttpTestingController), entries };
}

type Opened = Awaited<ReturnType<typeof open>>;

const verified = { ticket: 't-1', email: 'anna@firma.example', emailVerified: true, provider: 'google' };

async function link(opened: Opened, password: string) {
  typeInto(opened.harness.fixture, '#password', password);
  submitForm(opened.harness.fixture);
  await settle(opened.harness.fixture);
}

function stored(): string {
  return JSON.stringify({ ...sessionStorage }) + JSON.stringify({ ...localStorage });
}

describe('ProviderLinkPage (spec 0011)', () => {
  afterEach(() => {
    history.replaceState(null, '', '/');
    sessionStorage.clear();
    localStorage.clear();
  });

  it('says which account and which provider, with the password, "Connect and sign in" and "Forgot your password?"', async () => {
    const { harness } = await open(verified);
    expect(pageText(harness.fixture)).toContain(
      'An account with anna@firma.example exists. Enter its password once to connect Google. After that Google alone signs you in.',
    );
    expect(pageText(harness.fixture)).toContain('Connect and sign in');
    expect(has(harness.fixture, 'a[href="/forgot"]')).toBe(true);
    expect(has(harness.fixture, 'a[href="/signup"]')).toBe(false);
    expect((harness.fixture.nativeElement as HTMLElement).querySelector<HTMLInputElement>('#email')?.readOnly).toBe(true);
    expect((harness.fixture.nativeElement as HTMLElement).querySelector<HTMLInputElement>('#password')?.type).toBe('password');
  });

  it('an unverified address: the other sentence and the way to sign up', async () => {
    const { harness } = await open({ ...verified, emailVerified: false, provider: 'microsoft' });
    expect(pageText(harness.fixture)).toContain(
      'If you have an account with anna@firma.example, enter its password once to connect Microsoft. If you do not, create an account with your email address.',
    );
    expect(has(harness.fixture, 'a[href="/signup"]')).toBe(true);
    expect(has(harness.fixture, '#password')).toBe(true);
  });

  it('without a remembered provider it says "Google or Microsoft"', async () => {
    const { harness } = await open({ ...verified, provider: null });
    expect(pageText(harness.fixture)).toContain('to connect Google or Microsoft');
    expect(pageText(harness.fixture)).toContain('After that Google or Microsoft alone signs you in.');
  });

  it.each(['github', 'constructor', '__proto__', 42, undefined])('a provider in the state that is not one (%j) reads as "Google or Microsoft"', async (provider) => {
    const { harness } = await open({ ...verified, provider });
    expect(pageText(harness.fixture)).toContain('to connect Google or Microsoft');
  });

  it('after the screen has read the ticket the history entry holds no ticket (state replaced, address kept, no new entry)', async () => {
    const opened = await open(verified);
    expect(JSON.stringify(history.state)).not.toContain('t-1');
    expect(history.state).not.toHaveProperty('ticket');
    expect(history.state).not.toHaveProperty('email');
    expect(window.location.pathname).toBe('/provider-link');
    expect(history.length).toBe(opened.entries + 1);
    // The ticket lives on in the screen's memory: the form still sends it.
    await link(opened, 'Pw-1 x');
    expect(opened.ctrl.expectOne('/auth/providers/link').request.body).toEqual({ ticket: 't-1', password: 'Pw-1 x' });
  });

  it.each(['/forgot', '/signup'])('leaving through %s and stepping back does not bring the ticket back: the screen goes to /login', async (target) => {
    const opened = await open({ ...verified, emailVerified: target === '/forgot' });
    clickOn(opened.harness.fixture, `a[href="${target}"]`);
    await settle(opened.harness.fixture);
    expect(opened.router.url).toBe(target);
    expect(JSON.stringify(history.state)).not.toContain('t-1');
    opened.router.setUpLocationChangeListener(); // the harness does not listen to the browser's Back, the application does
    await new Promise<void>((resolve) => {
      window.addEventListener('popstate', () => resolve(), { once: true });
      history.back();
    });
    await settle(opened.harness.fixture);
    await settle(opened.harness.fixture);
    expect(JSON.stringify(history.state)).not.toContain('t-1');
    expect(opened.router.url).toBe('/login'); // the entry has no state: no ticket, no form
    expect(has(opened.harness.fixture, '#password')).toBe(false);
    opened.ctrl.expectNone('/auth/providers/link');
  });

  it('the ticket is shown nowhere and kept in no address and no storage', async () => {
    const opened = await open(verified);
    expect(pageText(opened.harness.fixture)).not.toContain('t-1');
    expect(window.location.href).not.toContain('t-1');
    expect(stored()).not.toContain('t-1');
    await link(opened, 'Pw-1 x');
    expect(stored()).not.toContain('Pw-1 x');
    expect(window.location.href).not.toContain('Pw-1');
    opened.ctrl.expectOne('/auth/providers/link');
  });

  it('the right password signs in and opens the notes, replacing this screen', async () => {
    const opened = await open(verified);
    await link(opened, 'Pw-1 x');
    const request = opened.ctrl.expectOne('/auth/providers/link');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ ticket: 't-1', password: 'Pw-1 x' });
    request.flush({ status: 'authenticated', access_token: 'tok-l' });
    await settle();
    opened.ctrl.expectOne('/auth/me').flush(privateMe);
    await settle(opened.harness.fixture);
    await settle(opened.harness.fixture);
    expect(opened.router.url).toBe('/notes');
    expect(window.location.pathname).toBe('/notes');
    expect(history.length).toBe(opened.entries + 1); // the entry of /provider-link was replaced: only the one it was opened from is before it
    expect(TestBed.inject(AuthService).token()).toBe('tok-l');
  });

  it('the password is sent as typed, spaces included', async () => {
    const opened = await open(verified);
    await link(opened, '  Pw-1 x  ');
    expect(opened.ctrl.expectOne('/auth/providers/link').request.body).toEqual({ ticket: 't-1', password: '  Pw-1 x  ' });
  });

  it('an empty password is not sent', async () => {
    const opened = await open(verified);
    await link(opened, '');
    opened.ctrl.expectNone('/auth/providers/link');
  });

  it('while the request runs the button is off and a second submit sends nothing', async () => {
    const opened = await open(verified);
    await link(opened, 'Pw-1 x');
    expect((opened.harness.fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('button[type="submit"]')?.disabled).toBe(true);
    submitForm(opened.harness.fixture);
    await settle(opened.harness.fixture);
    expect(opened.ctrl.match('/auth/providers/link')).toHaveLength(1);
  });

  it.each([
    [401, { error: 'invalid_credentials' }, "Wrong password. No password yet? Use 'Forgot your password?' to set one."],
    [429, { error: 'too_many_attempts', retry_after_seconds: 90 }, 'Too many attempts. Try again in 2 minutes.'],
    [400, { error: 'invalid_ticket' }, 'This sign-in took too long. Start again.'],
    [403, { error: 'email_not_verified' }, 'Your email address is not confirmed yet.'],
    [403, { error: 'no_membership' }, 'Your account does not belong to a company.'],
    [403, { error: 'no_email' }, 'Your Microsoft or Google account has no email address we can use. Sign in with your password.'],
    [400, { error: 'invalid_request' }, 'Something went wrong. Try again.'],
    [500, 'oops', 'Something went wrong. Try again.'],
  ])('%s %j says "%s"', async (code, body, text) => {
    const opened = await open(verified);
    await link(opened, 'Pw-1 x');
    opened.ctrl.expectOne('/auth/providers/link').flush(body, status(code));
    await settle(opened.harness.fixture);
    expect(pageText(opened.harness.fixture)).toContain(text);
    expect(opened.router.url).toBe('/provider-link');
    expect(TestBed.inject(AuthService).token()).toBeNull();
  });

  it('a refusal that leaves the ticket usable keeps the form, and the next try is sent again with the same ticket', async () => {
    const opened = await open(verified);
    await link(opened, 'wrong');
    opened.ctrl.expectOne('/auth/providers/link').flush({ error: 'invalid_credentials' }, status(401));
    await settle(opened.harness.fixture);
    expect(has(opened.harness.fixture, 'form')).toBe(true);
    expect((opened.harness.fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('button[type="submit"]')?.disabled).toBe(false);
    typeInto(opened.harness.fixture, '#password', 'Pw-1 x');
    submitForm(opened.harness.fixture);
    await settle(opened.harness.fixture);
    expect(opened.ctrl.expectOne('/auth/providers/link').request.body).toEqual({ ticket: 't-1', password: 'Pw-1 x' });
  });

  it('a lockout of 90 seconds says two minutes, and one of 30 seconds says one minute', async () => {
    const opened = await open(verified);
    await link(opened, 'x');
    opened.ctrl.expectOne('/auth/providers/link').flush({ error: 'too_many_attempts', retry_after_seconds: 30 }, status(429));
    await settle(opened.harness.fixture);
    expect(pageText(opened.harness.fixture)).toContain('Too many attempts. Try again in 1 minute.');
  });

  it('an expired ticket replaces the form with the sentence and the way back', async () => {
    const opened = await open(verified);
    await link(opened, 'Pw-1 x');
    opened.ctrl.expectOne('/auth/providers/link').flush({ error: 'invalid_ticket' }, status(400));
    await settle(opened.harness.fixture);
    expect(pageText(opened.harness.fixture)).toContain('This sign-in took too long. Start again.');
    expect(has(opened.harness.fixture, 'a[href="/login"]')).toBe(true);
    expect(has(opened.harness.fixture, '#password')).toBe(false);
    expect(pageText(opened.harness.fixture)).not.toContain('Enter its password');
    expect(pageText(opened.harness.fixture)).not.toContain('anna@firma.example');
    expect(has(opened.harness.fixture, '[data-testid="link-intro"]')).toBe(false);
    expect(has(opened.harness.fixture, 'a[href="/signup"]')).toBe(false);
    expect(has(opened.harness.fixture, 'a[href="/forgot"]')).toBe(false);
  });

  it('an expired ticket on an unverified address also drops the intro and the sign-up link', async () => {
    const opened = await open({ ...verified, emailVerified: false });
    await link(opened, 'Pw-1 x');
    opened.ctrl.expectOne('/auth/providers/link').flush({ error: 'invalid_ticket' }, status(400));
    await settle(opened.harness.fixture);
    expect(pageText(opened.harness.fixture)).toContain('This sign-in took too long. Start again.');
    expect(has(opened.harness.fixture, '[data-testid="link-intro"]')).toBe(false);
    expect(has(opened.harness.fixture, 'a[href="/signup"]')).toBe(false);
  });

  it('a 429 of the request limit leaves no message here (the bar says it) and the form works again', async () => {
    const opened = await open(verified);
    await link(opened, 'Pw-1 x');
    opened.ctrl.expectOne('/auth/providers/link').flush({ error: 'too_many_requests', retry_after_seconds: 5 }, status(429));
    await settle(opened.harness.fixture);
    expect(has(opened.harness.fixture, '[data-testid="link-error"]')).toBe(false);
    expect(pageText(opened.harness.fixture)).not.toContain('Something went wrong');
    expect((opened.harness.fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('button[type="submit"]')?.disabled).toBe(false);
    expect(opened.router.url).toBe('/provider-link');
  });

  it('an answer of 200 that is no sign-in says "Something went wrong"', async () => {
    const opened = await open(verified);
    await link(opened, 'Pw-1 x');
    opened.ctrl.expectOne('/auth/providers/link').flush({ status: 'other' });
    await settle(opened.harness.fixture);
    expect(pageText(opened.harness.fixture)).toContain('Something went wrong. Try again.');
    expect(opened.router.url).toBe('/provider-link');
  });

  it.each([
    null,
    { email: 'a@b.example' },
    { ...verified, ticket: '' },
    { ...verified, ticket: 5 },
    { ...verified, ticket: undefined },
    { ...verified, email: undefined },
    { ...verified, email: 5 },
    { ...verified, emailVerified: 'true' },
    { ...verified, emailVerified: undefined },
  ])('opened without its state (%j): /login, replacing this entry', async (state) => {
    const { router, ctrl, entries } = await open(state);
    expect(router.url).toBe('/login');
    expect(history.length).toBe(entries + 1); // the entry of /provider-link was replaced by /login
    ctrl.expectNone('/auth/providers/link');
  });
});
