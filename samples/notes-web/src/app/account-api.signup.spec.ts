import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { AccountApi } from './account-api';

const status = (code: number) => ({ status: code, statusText: String(code) });

describe('AccountApi: sign-up', () => {
  let api: AccountApi;
  let ctrl: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    api = TestBed.inject(AccountApi);
    ctrl = TestBed.inject(HttpTestingController);
  });

  afterEach(() => ctrl.verify());

  describe('options', () => {
    it('asks GET /auth/options and reads self-service', async () => {
      const done = api.options();
      const request = ctrl.expectOne('/auth/options');
      expect(request.request.method).toBe('GET');
      request.flush({ signup: 'self-service' });
      expect(await done).toEqual({ signup: 'self-service' });
    });

    it('ignores the members it does not know', async () => {
      const done = api.options();
      ctrl.expectOne('/auth/options').flush({ signup: 'self-service', providers: ['google'] });
      expect(await done).toEqual({ signup: 'self-service' });
    });

    it.each([[{ signup: 'invite-only' }], [{ signup: 'something-new' }], [{ signup: 1 }], [{}], [[]], ['<html>oops</html>'], [null]])(
      'the answer %j means invite-only: the link is not offered',
      async (body) => {
        const done = api.options();
        ctrl.expectOne('/auth/options').flush(body);
        expect(await done).toEqual({ signup: 'invite-only' });
      },
    );

    it.each([500, 502, 429, 403])('a %s means invite-only', async (code) => {
      const done = api.options();
      ctrl.expectOne('/auth/options').flush('oops', status(code));
      expect(await done).toEqual({ signup: 'invite-only' });
    });

    it('no network means invite-only', async () => {
      const done = api.options();
      ctrl.expectOne('/auth/options').error(new ProgressEvent('error'));
      expect(await done).toEqual({ signup: 'invite-only' });
    });
  });

  it('signUp posts the email and the company name to its endpoint and is ok on the empty answer', async () => {
    const done = api.signUp('anna@firma.example', 'Firma');
    const request = ctrl.expectOne('/auth/signup');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ email: 'anna@firma.example', company_name: 'Firma' });
    request.flush(null, status(202));
    expect((await done).ok).toBe(true);
  });

  it('reads the answer to a preview: the address and the company name', async () => {
    const done = api.previewSignup('tok');
    const request = ctrl.expectOne('/auth/signup/preview');
    expect(request.request.body).toEqual({ token: 'tok' });
    request.flush({ email: 'anna@firma.example', company_name: 'Firma' });
    expect(await done).toEqual({ ok: true, value: { email: 'anna@firma.example', company_name: 'Firma' } });
  });

  it('a 400 invalid_request on a preview stays "other": that screen has the common text', async () => {
    const done = api.previewSignup('tok');
    ctrl.expectOne('/auth/signup/preview').flush({ error: 'invalid_request' }, status(400));
    expect(await done).toEqual({ ok: false, failure: { kind: 'other' } });
  });

  it('a preview that is not what the contract says is a failure, not a half-filled screen', async () => {
    const done = api.previewSignup('tok');
    ctrl.expectOne('/auth/signup/preview').flush({ email: 'anna@firma.example' });
    expect(await done).toEqual({ ok: false, failure: { kind: 'other' } });
  });

  it.each([
    [403, { error: 'signup_disabled' }, { kind: 'signup_disabled' }],
    [409, { error: 'already_registered' }, { kind: 'already_registered' }],
    [400, { error: 'invalid_token' }, { kind: 'invalid_token' }],
    [400, { error: 'weak_password', rules: ['too_short'] }, { kind: 'weak_password', rules: ['too_short'] }],
    [429, { error: 'too_many_attempts', retry_after_seconds: 42 }, { kind: 'too_many_attempts', retryAfterSeconds: 42 }],
    [403, { error: 'forbidden' }, { kind: 'other' }],
    [409, { error: 'something_else' }, { kind: 'other' }],
    [400, { error: 'invalid_request' }, { kind: 'invalid_request' }],
    [500, '<html>oops</html>', { kind: 'other' }],
  ])('answer %s %j is the failure %j', async (code, body, failure) => {
    const done = api.signUp('anna@firma.example', 'Firma');
    ctrl.expectOne('/auth/signup').flush(body, status(code));
    expect(await done).toEqual({ ok: false, failure });
  });
});
