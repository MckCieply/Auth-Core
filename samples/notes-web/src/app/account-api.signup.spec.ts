import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { vi } from 'vitest';
import { AccountApi, OPTIONS_TIMEOUT_MS } from './account-api';

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
      expect(await done).toEqual({ signup: 'self-service', private_signup: 'off' });
    });

    it('ignores the members it does not know', async () => {
      const done = api.options();
      ctrl.expectOne('/auth/options').flush({ signup: 'self-service', providers: ['google'] });
      expect(await done).toEqual({ signup: 'self-service', private_signup: 'off' });
    });

    it('reads private_signup: self-service, and anything else as off (spec 0010)', async () => {
      const done = api.options();
      ctrl.expectOne('/auth/options').flush({ signup: 'invite-only', private_signup: 'self-service' });
      expect(await done).toEqual({ signup: 'invite-only', private_signup: 'self-service' });

      const other = api.options();
      ctrl.expectOne('/auth/options').flush({ signup: 'self-service', private_signup: 'on' });
      expect(await other).toEqual({ signup: 'self-service', private_signup: 'off' });
    });

    it.each([[{ signup: 'invite-only' }], [{ signup: 'something-new' }], [{ signup: 1 }], [{}], [[]], ['<html>oops</html>'], [null]])(
      'the answer %j means invite-only: the link is not offered',
      async (body) => {
        const done = api.options();
        ctrl.expectOne('/auth/options').flush(body);
        expect(await done).toEqual({ signup: 'invite-only', private_signup: 'off' });
      },
    );

    it.each([500, 502, 429, 403])('a %s means invite-only', async (code) => {
      const done = api.options();
      ctrl.expectOne('/auth/options').flush('oops', status(code));
      expect(await done).toEqual({ signup: 'invite-only', private_signup: 'off' });
    });

    it('an answer that does not come within ten seconds means invite-only, and the request is cancelled', async () => {
      vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval'] }); // the timeout of rxjs runs on setInterval
      try {
        const done = api.options();
        const request = ctrl.expectOne('/auth/options');
        vi.advanceTimersByTime(OPTIONS_TIMEOUT_MS - 1);
        expect(request.cancelled).toBe(false);
        vi.advanceTimersByTime(2);
        expect(await done).toEqual({ signup: 'invite-only', private_signup: 'off' });
        expect(request.cancelled).toBe(true);
      } finally {
        vi.useRealTimers();
      }
    });

    it('no network means invite-only', async () => {
      const done = api.options();
      ctrl.expectOne('/auth/options').error(new ProgressEvent('error'));
      expect(await done).toEqual({ signup: 'invite-only', private_signup: 'off' });
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

  it('signUp of a private person posts the email and the kind, and no company name (spec 0010)', async () => {
    const done = api.signUp('ola@poczta.example', null);
    const request = ctrl.expectOne('/auth/signup');
    expect(request.request.body).toEqual({ email: 'ola@poczta.example', kind: 'private' });
    request.flush(null, status(202));
    expect((await done).ok).toBe(true);
  });

  it('reads the preview of an organization: the kind, the address and the company name', async () => {
    const done = api.previewSignup('tok');
    const request = ctrl.expectOne('/auth/signup/preview');
    expect(request.request.body).toEqual({ token: 'tok' });
    request.flush({ kind: 'organization', email: 'anna@firma.example', company_name: 'Firma' });
    expect(await done).toEqual({ ok: true, value: { kind: 'organization', email: 'anna@firma.example', company_name: 'Firma' } });
  });

  it('reads the preview of a private sign-up: the kind and the address, no company name (spec 0010)', async () => {
    const done = api.previewSignup('tok');
    ctrl.expectOne('/auth/signup/preview').flush({ kind: 'private', email: 'ola@poczta.example' });
    expect(await done).toEqual({ ok: true, value: { kind: 'private', email: 'ola@poczta.example', company_name: null } });
  });

  it('a 400 invalid_request on a preview stays "other": that screen has the common text', async () => {
    const done = api.previewSignup('tok');
    ctrl.expectOne('/auth/signup/preview').flush({ error: 'invalid_request' }, status(400));
    expect(await done).toEqual({ ok: false, failure: { kind: 'other' } });
  });

  it.each([
    [{ email: 'anna@firma.example' }],
    [{ kind: 'organization', email: 'anna@firma.example' }],
    [{ kind: 'organization', email: 'anna@firma.example', company_name: 1 }],
    [{ kind: 'organization', company_name: 'Firma' }],
    [{ kind: 'private' }],
    [{ kind: 'private', email: 1 }],
    [{}],
    [[]],
    ['<html>oops</html>'],
    [null],
  ])('a preview that is not what the contract says (%j) is a failure, not a half-filled screen', async (body) => {
    const done = api.previewSignup('tok');
    ctrl.expectOne('/auth/signup/preview').flush(body);
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
