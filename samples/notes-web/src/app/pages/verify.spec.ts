import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { has, pageText, settle, submitForm, typeInto, typeRaw } from '../../testing/helpers';
import { VerifyPage } from './verify';

const status = (code: number) => ({ status: code, statusText: String(code) });
const invalid = 'This link has expired or was already used.';

async function open(query = '?token=tok-9') {
  TestBed.configureTestingModule({
    providers: [provideRouter([{ path: 'verify', component: VerifyPage }]), provideHttpClient(), provideHttpClientTesting()],
  });
  history.replaceState(null, '', `/verify${query}`);
  const harness = await RouterTestingHarness.create();
  await harness.navigateByUrl(`/verify${query}`, VerifyPage);
  return { fixture: harness.fixture, ctrl: TestBed.inject(HttpTestingController) };
}

describe('VerifyPage', () => {
  afterEach(() => history.replaceState(null, '', '/'));

  it('calls POST /auth/email/verify with the token on opening, and takes the token out of the address bar', async () => {
    const { fixture, ctrl } = await open();
    const request = ctrl.expectOne('/auth/email/verify');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ token: 'tok-9' });
    expect(window.location.search).toBe('');
    expect(pageText(fixture)).toContain('Confirming your email...');
    request.flush(null, status(204));
  });

  it('204: "Email confirmed." with a link to /login', async () => {
    const { fixture, ctrl } = await open();
    ctrl.expectOne('/auth/email/verify').flush(null, status(204));
    await settle(fixture);
    expect(pageText(fixture)).toContain('Email confirmed.');
    expect(has(fixture, 'a[href="/login"]')).toBe(true);
  });

  describe('invalid_token', () => {
    async function expired() {
      const opened = await open();
      opened.ctrl.expectOne('/auth/email/verify').flush({ error: 'invalid_token' }, status(400));
      await settle(opened.fixture);
      return opened;
    }

    it('says so and offers a form to send a new link', async () => {
      const { fixture } = await expired();
      expect(pageText(fixture)).toContain(invalid);
      expect(has(fixture, '#email')).toBe(true);
      expect(pageText(fixture)).toContain('Send the verification link again');
    });

    it('the form asks for a new link for the address typed, and says so', async () => {
      const { fixture, ctrl } = await expired();
      typeRaw(fixture, '#email', ' new@example.test ');
      submitForm(fixture);
      const request = ctrl.expectOne('/auth/email/verify/request');
      expect(request.request.body).toEqual({ email: 'new@example.test' });
      request.flush(null, status(202));
      await settle(fixture);
      expect(pageText(fixture)).toContain('If this address needs confirming, we sent a new link.');
    });

    it('429 on the form: "Wait N seconds before asking again."', async () => {
      const { fixture, ctrl } = await expired();
      typeInto(fixture, '#email', 'new@example.test');
      submitForm(fixture);
      ctrl.expectOne('/auth/email/verify/request').flush({ error: 'too_many_attempts', retry_after_seconds: 30 }, status(429));
      await settle(fixture);
      expect(pageText(fixture)).toContain('Wait 30 seconds before asking again.');
    });

    it('sends nothing for an empty address', async () => {
      const { fixture, ctrl } = await expired();
      submitForm(fixture);
      ctrl.expectNone('/auth/email/verify/request');
    });
  });

  it.each([
    [400, { error: 'invalid_request' }],
    [500, '<html>oops</html>'],
  ])('%s: "Something went wrong. Try again."', async (code, body) => {
    const { fixture, ctrl } = await open();
    ctrl.expectOne('/auth/email/verify').flush(body, status(code));
    await settle(fixture);
    expect(pageText(fixture)).toContain('Something went wrong. Try again.');
  });

  it.each(['', '?token=', '?other=1'])('a link with the query %j is an invalid link, with no request', async (query) => {
    const { fixture, ctrl } = await open(query);
    expect(pageText(fixture)).toContain(invalid);
    ctrl.expectNone('/auth/email/verify');
  });
});
