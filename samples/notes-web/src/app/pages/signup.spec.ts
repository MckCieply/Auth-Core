import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { has, pageText, realLocation, settle, submitForm, typeInto, typeRaw } from '../../testing/helpers';
import { SignupPage } from './signup';

const status = (code: number) => ({ status: code, statusText: String(code) });
const sent = 'Check your inbox. We sent a message to this address.';
const closed = 'Sign-up is closed. To join a company, ask it for an invitation.';

async function open() {
  TestBed.configureTestingModule({
    providers: [
      provideRouter([{ path: 'signup', component: SignupPage }]),
      provideHttpClient(),
      provideHttpClientTesting(),
      realLocation, // the router writes to the real window.location, so the address-bar test below can fail
    ],
  });
  history.replaceState(null, '', '/signup');
  const harness = await RouterTestingHarness.create();
  await harness.navigateByUrl('/signup', SignupPage);
  const ctrl = TestBed.inject(HttpTestingController);
  ctrl.expectOne('/auth/options').flush({ signup: 'self-service', private_signup: 'off' }); // the form is drawn once the options are read
  await settle(harness.fixture);
  return { fixture: harness.fixture, ctrl };
}

type Opened = Awaited<ReturnType<typeof open>>;

function ask({ fixture }: Opened, email: string, company: string): void {
  typeInto(fixture, '#email', email);
  typeInto(fixture, '#company', company);
  submitForm(fixture);
}

describe('SignupPage', () => {
  afterEach(() => history.replaceState(null, '', '/'));

  it('asks for an email and a company name and links back to the sign-in', async () => {
    const { fixture } = await open();
    expect(has(fixture, '#email')).toBe(true);
    expect(has(fixture, '#company')).toBe(true);
    expect(has(fixture, '#password')).toBe(false); // the password is chosen at the link, never on this form
    expect(has(fixture, 'a[href="/login"]')).toBe(true);
    expect((fixture.nativeElement as HTMLElement).querySelector<HTMLInputElement>('#company')?.maxLength).toBe(100);
  });

  it.each(['anna@firma.example', 'nobody@nowhere.example'])('202 for %s: always the same sentence, and the form sends the trimmed values', async (email) => {
    const opened = await open();
    typeRaw(opened.fixture, '#email', ` ${email} `);
    typeRaw(opened.fixture, '#company', ' Firma ');
    submitForm(opened.fixture);
    const request = opened.ctrl.expectOne('/auth/signup');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ email, company_name: 'Firma' });
    request.flush(null, status(202));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain(sent);
    expect(has(opened.fixture, '[data-testid="signup-sent"]')).toBe(true);
  });

  it('429 too_many_attempts: "Wait N seconds before asking again."', async () => {
    const opened = await open();
    ask(opened, 'anna@firma.example', 'Firma');
    opened.ctrl.expectOne('/auth/signup').flush({ error: 'too_many_attempts', retry_after_seconds: 42 }, status(429));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('Wait 42 seconds before asking again.');
    expect(pageText(opened.fixture)).not.toContain(sent);
  });

  it('403 signup_disabled: says that sign-up is closed and shows no form', async () => {
    const opened = await open();
    ask(opened, 'anna@firma.example', 'Firma');
    opened.ctrl.expectOne('/auth/signup').flush({ error: 'signup_disabled' }, status(403));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain(closed);
    expect(has(opened.fixture, 'form')).toBe(false);
    expect(pageText(opened.fixture)).not.toContain(sent);
    expect(has(opened.fixture, 'a[href="/login"]')).toBe(true);
  });

  it('400 invalid_request: says what to check', async () => {
    const opened = await open();
    ask(opened, 'anna@firma.example', 'Firma');
    opened.ctrl.expectOne('/auth/signup').flush({ error: 'invalid_request' }, status(400));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('Check the email address and the company name.');
    expect(pageText(opened.fixture)).not.toContain(sent);
    expect(has(opened.fixture, 'form')).toBe(true);
  });

  it('500 with a page of HTML: "Something went wrong. Try again."', async () => {
    const opened = await open();
    ask(opened, 'anna@firma.example', 'Firma');
    opened.ctrl.expectOne('/auth/signup').flush('<html>oops</html>', status(500));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('Something went wrong. Try again.');
    expect(pageText(opened.fixture)).not.toContain(sent);
  });

  it('no network: "Something went wrong. Try again."', async () => {
    const opened = await open();
    ask(opened, 'anna@firma.example', 'Firma');
    opened.ctrl.expectOne('/auth/signup').error(new ProgressEvent('error'));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('Something went wrong. Try again.');
  });

  it.each([
    ['', 'Firma'],
    ['   ', 'Firma'],
    ['anna@firma.example', ''],
    ['anna@firma.example', '   '],
  ])('sends nothing for the email %j and the company %j', async (email, company) => {
    const opened = await open();
    typeRaw(opened.fixture, '#email', email);
    typeRaw(opened.fixture, '#company', company);
    submitForm(opened.fixture);
    opened.ctrl.expectNone('/auth/signup');
  });

  it('sends one request for a double submit', async () => {
    const opened = await open();
    ask(opened, 'anna@firma.example', 'Firma');
    submitForm(opened.fixture);
    opened.ctrl.expectOne('/auth/signup').flush(null, status(202));
  });

  it('puts neither the email nor the company in the address bar', async () => {
    const opened = await open();
    ask(opened, 'anna@firma.example', 'Firma');
    opened.ctrl.expectOne('/auth/signup').flush(null, status(202));
    await settle(opened.fixture);
    expect(TestBed.inject(Router).url).toBe('/signup');
    expect(window.location.pathname).toBe('/signup');
    expect(window.location.search).toBe('');
    expect(window.location.href).not.toContain('anna');
    expect(window.location.href).not.toContain('Firma');
  });
});
