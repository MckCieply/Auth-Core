import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { vi } from 'vitest';
import { adminMe, has, pageText, realLocation, settle, submitForm, typeInto, typeRaw, valueOf } from '../../testing/helpers';
import { AuthService } from '../auth/auth.service';
import { CompleteSignupPage } from './complete-signup';

const status = (code: number) => ({ status: code, statusText: String(code) });
const invalid = 'This link has expired or was already used.';
const closed = 'Sign-up is closed. Ask the company you want to join for an invitation.';
const preview = { email: 'anna@firma.example', company_name: 'Firma' };
/** What Auth-Core answers a completion: a login's answer. */
const signedIn = { status: 'authenticated', access_token: 'tok-c' };

async function open(query = '?token=tok-s', previewAnswer: 'ok' | 'wait' = 'ok') {
  TestBed.configureTestingModule({
    providers: [
      provideRouter([{ path: 'complete-signup', component: CompleteSignupPage }]),
      provideHttpClient(),
      provideHttpClientTesting(),
      realLocation,
    ],
  });
  history.replaceState(null, '', `/complete-signup${query}`);
  const harness = await RouterTestingHarness.create();
  await harness.navigateByUrl(`/complete-signup${query}`, CompleteSignupPage);
  await settle(); // the navigation that takes the token out of the address bar lands first
  const navigateByUrl = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
  const ctrl = TestBed.inject(HttpTestingController);
  if (previewAnswer === 'ok') {
    ctrl.expectOne('/auth/signup/preview').flush(preview);
    await settle(harness.fixture);
  }
  return { fixture: harness.fixture, ctrl, navigateByUrl };
}

type Opened = Awaited<ReturnType<typeof open>>;

function choose({ fixture }: Opened, password: string, repeat = password): void {
  typeInto(fixture, '#password', password);
  typeInto(fixture, '#repeat', repeat);
  submitForm(fixture);
}

function button(opened: Opened): HTMLButtonElement | null {
  return (opened.fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('button[type="submit"]');
}

/** Answers the completion as Auth-Core does, then the /auth/me that signing in asks for. */
async function signInBy(opened: Opened, completion: TestRequest): Promise<void> {
  completion.flush(signedIn);
  await settle();
  opened.ctrl.expectOne('/auth/me').flush(adminMe);
  await settle(opened.fixture);
}

describe('CompleteSignupPage', () => {
  afterEach(() => history.replaceState(null, '', '/'));

  it('asks for the preview with the token on opening, once, and takes the token out of the address bar and the router', async () => {
    const { ctrl } = await open('?token=tok-s', 'wait');
    const request = ctrl.expectOne('/auth/signup/preview');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ token: 'tok-s' });
    expect(window.location.search).toBe('');
    expect(window.location.href).not.toContain('tok-s');
    expect(TestBed.inject(Router).url).toBe('/complete-signup');
    request.flush(preview);
    await settle();
    ctrl.expectNone('/auth/signup/preview'); // the navigation that removed the token did not run the screen again
  });

  it('shows the address (not editable), the company name (editable) and who the password is for', async () => {
    const { fixture } = await open();
    expect(valueOf(fixture, '#email')).toBe('anna@firma.example');
    expect((fixture.nativeElement as HTMLElement).querySelector<HTMLInputElement>('#email')?.readOnly).toBe(true);
    expect(valueOf(fixture, '#company')).toBe('Firma');
    expect((fixture.nativeElement as HTMLElement).querySelector<HTMLInputElement>('#company')?.readOnly).toBe(false);
    expect((fixture.nativeElement as HTMLElement).querySelector<HTMLInputElement>('#company')?.maxLength).toBe(100);
    expect(pageText(fixture)).toContain('This sets the password for anna@firma.example.');
    expect(has(fixture, '#password')).toBe(true);
    expect(has(fixture, '#repeat')).toBe(true);
  });

  it('200: signs the person in and opens the notes screen in place of this one; the address and the token are in no URL', async () => {
    const opened = await open();
    choose(opened, 'Chosen-Passw0rd');
    const request = opened.ctrl.expectOne('/auth/signup/complete');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ token: 'tok-s', password: 'Chosen-Passw0rd', company_name: 'Firma' });
    await signInBy(opened, request);
    const auth = TestBed.inject(AuthService);
    expect(auth.token()).toBe('tok-c');
    expect(auth.me()).toEqual(adminMe);
    // `replaceUrl`: Back from the notes screen must not land on this screen again, whose link is used up.
    expect(opened.navigateByUrl).toHaveBeenCalledTimes(1);
    expect(opened.navigateByUrl).toHaveBeenCalledWith('/notes', { replaceUrl: true });
    expect(window.location.href).not.toContain('anna@firma.example');
    expect(window.location.href).not.toContain('tok-c');
  });

  it('200 when /auth/me cannot be read: the person is signed in and the notes screen opens (it asks for the person again)', async () => {
    const opened = await open();
    choose(opened, 'Chosen-Passw0rd');
    opened.ctrl.expectOne('/auth/signup/complete').flush(signedIn);
    await settle();
    opened.ctrl.expectOne('/auth/me').flush('oops', status(500));
    await settle(opened.fixture);
    expect(TestBed.inject(AuthService).token()).toBe('tok-c');
    expect(opened.navigateByUrl).toHaveBeenCalledWith('/notes', { replaceUrl: true });
  });

  it('a 200 that is not a sign-in: "Something went wrong. Try again.", the form stays, and nothing opens', async () => {
    const opened = await open();
    choose(opened, 'Chosen-Passw0rd');
    opened.ctrl.expectOne('/auth/signup/complete').flush({ status: 'mfa_required' });
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('Something went wrong. Try again.');
    expect(has(opened.fixture, 'form')).toBe(true);
    expect(TestBed.inject(AuthService).token()).toBeNull();
    expect(opened.navigateByUrl).not.toHaveBeenCalled();
  });

  it('sends the company name the person typed, trimmed', async () => {
    const opened = await open();
    typeInto(opened.fixture, '#company', '  Firma Renamed  ');
    choose(opened, 'Chosen-Passw0rd');
    const request = opened.ctrl.expectOne('/auth/signup/complete');
    expect(request.request.body).toEqual({ token: 'tok-s', password: 'Chosen-Passw0rd', company_name: 'Firma Renamed' });
    await signInBy(opened, request);
  });

  it('sends the password exactly as typed', async () => {
    const opened = await open();
    choose(opened, '  Chosen Passw0rd  ');
    const request = opened.ctrl.expectOne('/auth/signup/complete');
    expect(request.request.body).toEqual({ token: 'tok-s', password: '  Chosen Passw0rd  ', company_name: 'Firma' });
    await signInBy(opened, request);
  });

  it('weak_password: the rules not met, one line each, and the form stays', async () => {
    const opened = await open();
    choose(opened, 'short');
    opened.ctrl
      .expectOne('/auth/signup/complete')
      .flush({ error: 'weak_password', rules: ['too_short', 'requires_digit'] }, status(400));
    await settle(opened.fixture);
    const lines = Array.from(
      (opened.fixture.nativeElement as HTMLElement).querySelectorAll('[data-testid="rules"] li'),
      (item) => item.textContent?.trim(),
    );
    expect(lines).toEqual(['The password is too short.', 'The password needs a digit.']);
    expect(has(opened.fixture, 'form')).toBe(true);
    expect(opened.navigateByUrl).not.toHaveBeenCalled();
    expect(TestBed.inject(AuthService).token()).toBeNull();
  });

  it('409 already_registered: says the account exists and links to the sign-in; no form', async () => {
    const opened = await open();
    choose(opened, 'Chosen-Passw0rd');
    opened.ctrl.expectOne('/auth/signup/complete').flush({ error: 'already_registered' }, status(409));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('An account with this address already exists.');
    expect(has(opened.fixture, 'a[href="/login"]')).toBe(true);
    expect(has(opened.fixture, 'form')).toBe(false);
  });

  it.each(['preview', 'complete'])('invalid_token on the %s: the link is used up, with a link to sign up again; no form', async (where) => {
    const opened = await open('?token=tok-s', where === 'preview' ? 'wait' : 'ok');
    if (where === 'preview') {
      opened.ctrl.expectOne('/auth/signup/preview').flush({ error: 'invalid_token' }, status(400));
    } else {
      choose(opened, 'Chosen-Passw0rd');
      opened.ctrl.expectOne('/auth/signup/complete').flush({ error: 'invalid_token' }, status(400));
    }
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain(invalid);
    expect(has(opened.fixture, 'a[href="/signup"][data-testid="signup-again"]')).toBe(true);
    expect(pageText(opened.fixture)).toContain('Sign up again');
    expect(has(opened.fixture, 'form')).toBe(false);
  });

  it.each(['preview', 'complete'])('403 signup_disabled on the %s: says that sign-up is closed; no form', async (where) => {
    const opened = await open('?token=tok-s', where === 'preview' ? 'wait' : 'ok');
    if (where === 'preview') {
      opened.ctrl.expectOne('/auth/signup/preview').flush({ error: 'signup_disabled' }, status(403));
    } else {
      choose(opened, 'Chosen-Passw0rd');
      opened.ctrl.expectOne('/auth/signup/complete').flush({ error: 'signup_disabled' }, status(403));
    }
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain(closed);
    expect(pageText(opened.fixture)).toContain('Back to sign in');
    expect(has(opened.fixture, 'form')).toBe(false);
    expect(has(opened.fixture, 'a[href="/login"]')).toBe(true);
  });

  it.each([
    [400, { error: 'invalid_request' }],
    [500, '<html>oops</html>'],
  ])('%s on the preview: "Something went wrong. Try again."', async (code, body) => {
    const { fixture, ctrl } = await open('?token=tok-s', 'wait');
    ctrl.expectOne('/auth/signup/preview').flush(body, status(code));
    await settle(fixture);
    expect(pageText(fixture)).toContain('Something went wrong. Try again.');
  });

  it('"Try again" after a server error on the preview asks again with the token still held, and the form follows', async () => {
    const { fixture, ctrl } = await open('?token=tok-s', 'wait');
    ctrl.expectOne('/auth/signup/preview').flush('oops', status(500));
    await settle(fixture);
    expect(window.location.search).toBe('');
    const again = (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('[data-testid="try-again"]');
    expect(again?.textContent?.trim()).toBe('Try again');
    again?.click();
    fixture.detectChanges();
    expect(pageText(fixture)).toContain('Opening your sign-up...');
    const retry = ctrl.expectOne('/auth/signup/preview');
    expect(retry.request.body).toEqual({ token: 'tok-s' });
    retry.flush(preview);
    await settle(fixture);
    expect(has(fixture, 'form')).toBe(true);
    expect(has(fixture, '[data-testid="try-again"]')).toBe(false);
  });

  it('500 on complete: "Something went wrong. Try again.", and the form stays', async () => {
    const opened = await open();
    choose(opened, 'Chosen-Passw0rd');
    opened.ctrl.expectOne('/auth/signup/complete').flush('oops', status(500));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('Something went wrong. Try again.');
    expect(has(opened.fixture, 'form')).toBe(true);
  });

  it('two different passwords: a message, and no request', async () => {
    const opened = await open();
    choose(opened, 'Chosen-Passw0rd', 'Chosen-Passw0rd2');
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('The two passwords are not the same.');
    opened.ctrl.expectNone('/auth/signup/complete');
  });

  it('a blank company name: a message, and no request', async () => {
    const opened = await open();
    typeRaw(opened.fixture, '#company', '   ');
    choose(opened, 'Chosen-Passw0rd');
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('Enter the name of your company.');
    opened.ctrl.expectNone('/auth/signup/complete');
  });

  it('400 invalid_request on complete: says what to check, and the form stays', async () => {
    const opened = await open();
    choose(opened, 'Chosen-Passw0rd');
    opened.ctrl.expectOne('/auth/signup/complete').flush({ error: 'invalid_request' }, status(400));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('Check the company name.');
    expect(has(opened.fixture, 'form')).toBe(true);
  });

  it.each(['', '?token=', '?other=1'])('a link with the query %j is an invalid link, with no request', async (query) => {
    const { fixture, ctrl } = await open(query, 'wait');
    expect(pageText(fixture)).toContain(invalid);
    expect(has(fixture, 'a[href="/signup"]')).toBe(true);
    ctrl.expectNone('/auth/signup/preview');
  });

  it('sends one request for a double submit', async () => {
    const opened = await open();
    choose(opened, 'Chosen-Passw0rd');
    submitForm(opened.fixture);
    await signInBy(opened, opened.ctrl.expectOne('/auth/signup/complete'));
  });

  it.each([
    ['rejects', () => Promise.reject(new Error('navigation failed'))],
    ['answers false', () => Promise.resolve(false)],
  ])('the button is on again when the router %s (the person stays here, signed in)', async (_name, answer) => {
    const opened = await open();
    opened.navigateByUrl.mockImplementation(answer);
    choose(opened, 'Chosen-Passw0rd');
    await signInBy(opened, opened.ctrl.expectOne('/auth/signup/complete'));
    expect(button(opened)?.disabled).toBe(false);
    expect(TestBed.inject(AuthService).token()).toBe('tok-c');
  });

  it('after 200 the button stays off until the notes screen is open: a second Enter does not post the used token again', async () => {
    const opened = await open();
    let arrive: (opened: boolean) => void = () => undefined;
    opened.navigateByUrl.mockReturnValue(new Promise<boolean>((resolve) => (arrive = resolve)));
    choose(opened, 'Chosen-Passw0rd');
    await signInBy(opened, opened.ctrl.expectOne('/auth/signup/complete'));
    expect(button(opened)?.disabled).toBe(true);
    submitForm(opened.fixture);
    await settle(opened.fixture);
    opened.ctrl.expectNone('/auth/signup/complete');
    arrive(true);
    await settle(opened.fixture);
    expect(button(opened)?.disabled).toBe(false);
  });
});
