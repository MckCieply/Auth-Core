import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { vi } from 'vitest';
import { adminMe, settle, signedInAs, viewerMe } from '../../testing/helpers';
import { AuthService } from './auth.service';

const status = (code: number) => ({ status: code, statusText: String(code) });
const signedIn = { status: 'authenticated', access_token: 'tok-c' };

describe('AuthService.completeSignup', () => {
  let auth: AuthService;
  let ctrl: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    auth = TestBed.inject(AuthService);
    ctrl = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    vi.useRealTimers();
    localStorage.clear();
    sessionStorage.clear();
  });

  it('posts the token, the password and the company name, keeps the token in memory and loads /auth/me', async () => {
    const done = auth.completeSignup('tok', 'Pw-1 x', 'Firma');
    const request = ctrl.expectOne('/auth/signup/complete');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ token: 'tok', password: 'Pw-1 x', company_name: 'Firma' });
    request.flush(signedIn);
    await settle();
    ctrl.expectOne('/auth/me').flush(adminMe);
    expect(await done).toEqual({ ok: true });
    expect(auth.token()).toBe('tok-c');
    expect(auth.me()).toEqual(adminMe);
    ctrl.verify();
  });

  it('leaves the password out of the body when there is none to send (an account that exists, without a company)', async () => {
    const done = auth.completeSignup('tok', null, 'Firma');
    const request = ctrl.expectOne('/auth/signup/complete');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ token: 'tok', company_name: 'Firma' });
    expect('password' in (request.request.body as object)).toBe(false);
    request.flush(signedIn);
    await settle();
    ctrl.expectOne('/auth/me').flush(adminMe);
    expect(await done).toEqual({ ok: true });
    expect(auth.token()).toBe('tok-c');
    ctrl.verify();
  });

  it('never writes the token to localStorage, sessionStorage or a cookie', async () => {
    const done = auth.completeSignup('tok', 'pw', 'Firma');
    const completion = ctrl.expectOne('/auth/signup/complete');
    completion.flush({ status: 'authenticated', access_token: 'secret-token-value' });
    await settle();
    const me = ctrl.expectOne('/auth/me');
    me.flush(adminMe);
    expect(await done).toEqual({ ok: true });
    expect(auth.token()).toBe('secret-token-value');
    expect(window.location.href).not.toContain('secret-token-value');
    expect(completion.request.urlWithParams).not.toContain('secret-token-value');
    expect(me.request.urlWithParams).not.toContain('secret-token-value');
    expect(JSON.stringify({ ...localStorage })).not.toContain('secret-token-value');
    expect(JSON.stringify({ ...sessionStorage })).not.toContain('secret-token-value');
    expect(document.cookie).not.toContain('secret-token-value');
    expect(localStorage.length + sessionStorage.length).toBe(0);
  });

  it.each([
    [400, { error: 'invalid_token' }, { kind: 'invalid_token' }],
    [400, { error: 'weak_password', rules: ['too_short'] }, { kind: 'weak_password', rules: ['too_short'] }],
    [409, { error: 'already_registered' }, { kind: 'already_registered' }],
    [403, { error: 'signup_disabled' }, { kind: 'signup_disabled' }],
    [400, { error: 'invalid_request' }, { kind: 'invalid_request' }],
    [429, { error: 'too_many_requests', retry_after_seconds: 17 }, { kind: 'too_many_requests', retryAfterSeconds: 17 }],
    [500, '<html>oops</html>', { kind: 'other' }],
  ])('answer %s %j is the failure %j and leaves the person anonymous', async (code, body, failure) => {
    const done = auth.completeSignup('tok', 'pw', 'Firma');
    ctrl.expectOne('/auth/signup/complete').flush(body, status(code));
    expect(await done).toEqual({ ok: false, failure });
    expect(auth.token()).toBeNull();
    ctrl.verify();
  });

  it('a 200 that is not "authenticated" is not a session', async () => {
    const done = auth.completeSignup('tok', 'pw', 'Firma');
    ctrl.expectOne('/auth/signup/complete').flush({ status: 'mfa_required' });
    expect(await done).toEqual({ ok: false, failure: { kind: 'other' } });
    expect(auth.token()).toBeNull();
  });

  it('is signed in even when /auth/me cannot be read: the notes screen asks for the person again', async () => {
    const done = auth.completeSignup('tok', 'pw', 'Firma');
    ctrl.expectOne('/auth/signup/complete').flush(signedIn);
    await settle();
    ctrl.expectOne('/auth/me').flush('oops', status(500));
    expect(await done).toEqual({ ok: true });
    expect(auth.token()).toBe('tok-c');
    expect(auth.me()).toBeNull();
  });

  it('opening the link while another person is signed in: the old person is gone, and the new one is shown', async () => {
    await signedInAs(auth, ctrl, adminMe, 'tok-a');
    const done = auth.completeSignup('tok', 'pw', 'Firma');
    ctrl.expectOne('/auth/signup/complete').flush(signedIn);
    await settle();
    ctrl.expectOne('/auth/me').flush(viewerMe);
    expect(await done).toEqual({ ok: true });
    expect(auth.token()).toBe('tok-c');
    expect(auth.me()).toEqual(viewerMe);
    ctrl.verify();
  });

  it('a completion that fails leaves the session that exists alone', async () => {
    await signedInAs(auth, ctrl, adminMe, 'tok-a');
    const done = auth.completeSignup('tok', 'pw', 'Firma');
    ctrl.expectOne('/auth/signup/complete').flush({ error: 'invalid_token' }, status(400));
    expect((await done).ok).toBe(false);
    expect(auth.token()).toBe('tok-a');
    expect(auth.me()).toEqual(adminMe);
  });

  it('clears an old notice when the person is signed in', async () => {
    auth.showNotice('unreachable');
    const done = auth.completeSignup('tok', 'pw', 'Firma');
    ctrl.expectOne('/auth/signup/complete').flush(signedIn);
    await settle();
    ctrl.expectOne('/auth/me').flush(adminMe);
    await done;
    expect(auth.notice()).toBeNull();
  });
});
