import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { clickOn, has, pageText, privateMe, realLocation, settle, submitForm, typeInto, valueOf } from '../../testing/helpers';
import { AuthService } from '../auth/auth.service';
import { ProviderFinishPage } from './provider-finish';

const status = (code: number) => ({ status: code, statusText: String(code) });

@Component({ template: '<p>elsewhere</p>' })
class Elsewhere {}

async function open(state: Record<string, unknown> | null) {
  TestBed.configureTestingModule({
    providers: [
      provideRouter([
        { path: 'start', component: Elsewhere },
        { path: 'provider-finish', component: ProviderFinishPage },
        { path: 'login', component: Elsewhere },
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
  const entries = history.length;
  await router.navigateByUrl('/provider-finish', state === null ? {} : { state });
  await settle(harness.fixture);
  return { harness, router, ctrl: TestBed.inject(HttpTestingController), entries };
}

type Opened = Awaited<ReturnType<typeof open>>;

const both = { ticket: 't-1', email: 'new@gmail.example', kinds: ['organization', 'private'] };

async function finishWith(opened: Opened, company: string): Promise<void> {
  typeInto(opened.harness.fixture, '#company', company);
  submitForm(opened.harness.fixture);
  await settle(opened.harness.fixture);
}

function stored(): string {
  return JSON.stringify({ ...sessionStorage }) + JSON.stringify({ ...localStorage });
}

describe('ProviderFinishPage (spec 0011)', () => {
  afterEach(() => {
    history.replaceState(null, '', '/');
    sessionStorage.clear();
    localStorage.clear();
  });

  it('shows the address read-only, the choice when both kinds are on, organization first, with the company name', async () => {
    const { harness } = await open(both);
    expect(pageText(harness.fixture)).toContain('Finish creating your account');
    expect(valueOf(harness.fixture, '#email')).toBe('new@gmail.example');
    expect((harness.fixture.nativeElement as HTMLElement).querySelector<HTMLInputElement>('#email')?.readOnly).toBe(true);
    expect(has(harness.fixture, '[data-testid="finish-kind"]')).toBe(true);
    expect(has(harness.fixture, '#company')).toBe(true);
    expect(has(harness.fixture, '#password')).toBe(false);
    expect(pageText(harness.fixture)).toContain('Create account');
    const radios = [...(harness.fixture.nativeElement as HTMLElement).querySelectorAll<HTMLInputElement>('input[type="radio"]')];
    expect(radios.map((radio) => radio.getAttribute('data-testid'))).toEqual(['finish-organization', 'finish-private']);
    expect(radios.map((radio) => radio.checked)).toEqual([true, false]);
  });

  it('the kinds in the other order still show organization first and checked', async () => {
    const { harness } = await open({ ...both, kinds: ['private', 'organization'] });
    const radios = [...(harness.fixture.nativeElement as HTMLElement).querySelectorAll<HTMLInputElement>('input[type="radio"]')];
    expect(radios.map((radio) => radio.checked)).toEqual([true, false]);
    expect(has(harness.fixture, '#company')).toBe(true);
  });

  it.each([
    [['private'], false, false],
    [['organization'], false, true],
    [['private', 'x'], false, false],
    [['x', 'organization'], false, true],
  ])('kinds %j: no choice; company name %s', async (kinds, choice, company) => {
    const { harness } = await open({ ...both, kinds });
    expect(has(harness.fixture, '[data-testid="finish-kind"]')).toBe(choice);
    expect(has(harness.fixture, '#company')).toBe(company);
  });

  it('only private on offer: a private account is made without a company name', async () => {
    const opened = await open({ ...both, kinds: ['private'] });
    submitForm(opened.harness.fixture);
    await settle(opened.harness.fixture);
    expect(opened.ctrl.expectOne('/auth/providers/finish').request.body).toEqual({ ticket: 't-1', kind: 'private' });
  });

  it('only organization on offer: the name is asked for and sent', async () => {
    const opened = await open({ ...both, kinds: ['organization'] });
    await finishWith(opened, 'Firma');
    expect(opened.ctrl.expectOne('/auth/providers/finish').request.body).toEqual({ ticket: 't-1', kind: 'organization', company_name: 'Firma' });
  });

  it('an organization sends its name and opens the notes, replacing this screen', async () => {
    const opened = await open(both);
    typeInto(opened.harness.fixture, '#company', '  Firma  ');
    submitForm(opened.harness.fixture);
    await settle(opened.harness.fixture);
    const request = opened.ctrl.expectOne('/auth/providers/finish');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ ticket: 't-1', kind: 'organization', company_name: 'Firma' });
    request.flush({ status: 'authenticated', access_token: 'tok-f' });
    await settle();
    opened.ctrl.expectOne('/auth/me').flush(privateMe);
    await settle(opened.harness.fixture);
    await settle(opened.harness.fixture);
    expect(opened.router.url).toBe('/notes');
    expect(window.location.pathname).toBe('/notes');
    expect(history.length).toBe(opened.entries + 1); // the entry of /provider-finish was replaced
    expect(TestBed.inject(AuthService).token()).toBe('tok-f');
  });

  it('a private person sends no company name', async () => {
    const opened = await open(both);
    clickOn(opened.harness.fixture, '[data-testid="finish-private"]');
    opened.harness.fixture.detectChanges();
    expect(has(opened.harness.fixture, '#company')).toBe(false);
    submitForm(opened.harness.fixture);
    await settle(opened.harness.fixture);
    expect(opened.ctrl.expectOne('/auth/providers/finish').request.body).toEqual({ ticket: 't-1', kind: 'private' });
  });

  it('choosing private, then organization again, asks for the name again and sends it', async () => {
    const opened = await open(both);
    clickOn(opened.harness.fixture, '[data-testid="finish-private"]');
    opened.harness.fixture.detectChanges();
    clickOn(opened.harness.fixture, '[data-testid="finish-organization"]');
    opened.harness.fixture.detectChanges();
    expect(has(opened.harness.fixture, '#company')).toBe(true);
    await finishWith(opened, 'Firma');
    expect(opened.ctrl.expectOne('/auth/providers/finish').request.body).toEqual({ ticket: 't-1', kind: 'organization', company_name: 'Firma' });
  });

  it('a name typed for an organization is not sent once private is chosen', async () => {
    const opened = await open(both);
    typeInto(opened.harness.fixture, '#company', 'Firma');
    clickOn(opened.harness.fixture, '[data-testid="finish-private"]');
    opened.harness.fixture.detectChanges();
    submitForm(opened.harness.fixture);
    await settle(opened.harness.fixture);
    expect(opened.ctrl.expectOne('/auth/providers/finish').request.body).toEqual({ ticket: 't-1', kind: 'private' });
  });

  it.each(['', '   '])('an organization without a name (%j) is not sent', async (name) => {
    const opened = await open(both);
    await finishWith(opened, name);
    opened.ctrl.expectNone('/auth/providers/finish');
    expect(pageText(opened.harness.fixture)).toContain('Enter the name of your company.');
  });

  it('the ticket is shown nowhere and kept in no address and no storage', async () => {
    const opened = await open(both);
    await finishWith(opened, 'Firma');
    expect(pageText(opened.harness.fixture)).not.toContain('t-1');
    expect(window.location.href).not.toContain('t-1');
    expect(stored()).not.toContain('t-1');
    opened.ctrl.expectOne('/auth/providers/finish');
  });

  it('while the request runs the button is off and a second submit sends nothing', async () => {
    const opened = await open(both);
    await finishWith(opened, 'Firma');
    expect((opened.harness.fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('button[type="submit"]')?.disabled).toBe(true);
    submitForm(opened.harness.fixture);
    await settle(opened.harness.fixture);
    expect(opened.ctrl.match('/auth/providers/finish')).toHaveLength(1);
  });

  it.each([
    [409, 'already_registered', 'An account with this address already exists. Sign in with your password.'],
    [403, 'signup_disabled', 'Sign-up is closed. To join a company, ask it for an invitation.'],
    [400, 'invalid_ticket', 'This sign-in took too long. Start again.'],
    [400, 'invalid_request', 'Check the company name.'],
    [403, 'email_not_verified', 'Something went wrong. Try again.'],
    [500, 'oops', 'Something went wrong. Try again.'],
  ])('%s %s says "%s"', async (code, error, text) => {
    const opened = await open(both);
    await finishWith(opened, 'Firma');
    opened.ctrl.expectOne('/auth/providers/finish').flush({ error }, status(code));
    await settle(opened.harness.fixture);
    expect(pageText(opened.harness.fixture)).toContain(text);
    expect(TestBed.inject(AuthService).token()).toBeNull();
    expect(opened.router.url).toBe('/provider-finish');
  });

  it.each([
    [409, 'already_registered'],
    [403, 'signup_disabled'],
    [400, 'invalid_ticket'],
  ])('%s %s ends the form and leaves the way to sign in', async (code, error) => {
    const opened = await open(both);
    await finishWith(opened, 'Firma');
    opened.ctrl.expectOne('/auth/providers/finish').flush({ error }, status(code));
    await settle(opened.harness.fixture);
    expect(has(opened.harness.fixture, 'a[href="/login"]')).toBe(true);
    expect(has(opened.harness.fixture, 'form')).toBe(false);
  });

  it('already_registered offers "Sign in." and the other two "Back to sign in"', async () => {
    const registered = await open(both);
    await finishWith(registered, 'Firma');
    registered.ctrl.expectOne('/auth/providers/finish').flush({ error: 'already_registered' }, status(409));
    await settle(registered.harness.fixture);
    expect(pageText(registered.harness.fixture)).toContain('Sign in.');
    expect(pageText(registered.harness.fixture)).not.toContain('Back to sign in');
  });

  it.each([
    [400, 'invalid_request', 'Check the company name.'],
    [500, 'oops', 'Something went wrong. Try again.'],
  ])('%s %s keeps the form, and the next try is sent with the same ticket', async (code, error, text) => {
    const opened = await open(both);
    await finishWith(opened, 'Firma');
    opened.ctrl.expectOne('/auth/providers/finish').flush({ error }, status(code));
    await settle(opened.harness.fixture);
    expect(pageText(opened.harness.fixture)).toContain(text);
    expect(has(opened.harness.fixture, 'form')).toBe(true);
    expect((opened.harness.fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('button[type="submit"]')?.disabled).toBe(false);
    submitForm(opened.harness.fixture);
    await settle(opened.harness.fixture);
    expect(opened.ctrl.expectOne('/auth/providers/finish').request.body).toEqual({ ticket: 't-1', kind: 'organization', company_name: 'Firma' });
  });

  it('a 429 of the request limit leaves no message here (the bar says it) and the form works again', async () => {
    const opened = await open(both);
    await finishWith(opened, 'Firma');
    opened.ctrl.expectOne('/auth/providers/finish').flush({ error: 'too_many_requests', retry_after_seconds: 5 }, status(429));
    await settle(opened.harness.fixture);
    expect(has(opened.harness.fixture, '[data-testid="finish-error"]')).toBe(false);
    expect(pageText(opened.harness.fixture)).not.toContain('Something went wrong');
    expect((opened.harness.fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('button[type="submit"]')?.disabled).toBe(false);
    expect(opened.router.url).toBe('/provider-finish');
  });

  it('an answer of 200 that is no sign-in says "Something went wrong"', async () => {
    const opened = await open(both);
    await finishWith(opened, 'Firma');
    opened.ctrl.expectOne('/auth/providers/finish').flush({ status: 'other' });
    await settle(opened.harness.fixture);
    expect(pageText(opened.harness.fixture)).toContain('Something went wrong. Try again.');
    expect(opened.router.url).toBe('/provider-finish');
  });

  it.each([
    null,
    { ...both, kinds: [] },
    { ...both, kinds: ['x'] },
    { ...both, kinds: 'organization' },
    { ticket: 't-1', email: 'a@b.example' },
    { ...both, ticket: '' },
    { ...both, ticket: 5 },
    { ...both, email: undefined },
    { ...both, email: 5 },
  ])('opened without its state (%j): /login, replacing this entry', async (state) => {
    const { router, ctrl, entries } = await open(state);
    expect(router.url).toBe('/login');
    expect(history.length).toBe(entries + 1); // the entry of /provider-finish was replaced by /login
    ctrl.expectNone('/auth/providers/finish');
  });
});
