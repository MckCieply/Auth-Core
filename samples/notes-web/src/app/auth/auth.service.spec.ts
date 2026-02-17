import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { vi } from 'vitest';
import { adminMe, holdToken, settle, signedInAs, viewerMe } from '../../testing/helpers';
import { AuthService, failureOf } from './auth.service';

const status = (code: number) => ({ status: code, statusText: String(code) });

describe('AuthService', () => {
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

  describe('at start', () => {
    it('200: keeps the token, then asks /auth/me; the person is signed in', async () => {
      const started = auth.start();
      const refresh = ctrl.expectOne('/auth/refresh');
      expect(refresh.request.method).toBe('POST');
      refresh.flush({ access_token: 'tok-1' });
      await settle();
      ctrl.expectOne('/auth/me').flush(adminMe);
      await started;
      expect(auth.token()).toBe('tok-1');
      expect(auth.me()).toEqual(adminMe);
      expect(auth.notice()).toBeNull();
      ctrl.verify();
    });

    it('401: the person is anonymous and nothing is shown', async () => {
      const started = auth.start();
      ctrl.expectOne('/auth/refresh').flush({ error: 'invalid_grant' }, status(401));
      await started;
      expect(auth.token()).toBeNull();
      expect(auth.me()).toBeNull();
      expect(auth.notice()).toBeNull();
      ctrl.verify();
    });

    it('anything else: anonymous, and the bar says the server cannot be reached', async () => {
      const started = auth.start();
      ctrl.expectOne('/auth/refresh').flush('<html>bad gateway</html>', status(502));
      await started;
      expect(auth.token()).toBeNull();
      expect(auth.notice()).toBe('unreachable');
      ctrl.verify();
    });

    it('no network: anonymous, and the bar says the server cannot be reached', async () => {
      const started = auth.start();
      ctrl.expectOne('/auth/refresh').error(new ProgressEvent('error'));
      await started;
      expect(auth.token()).toBeNull();
      expect(auth.notice()).toBe('unreachable');
      ctrl.verify();
    });

    it('a 200 without a token is not a session', async () => {
      const started = auth.start();
      ctrl.expectOne('/auth/refresh').flush({});
      await started;
      expect(auth.token()).toBeNull();
      expect(auth.notice()).toBe('unreachable');
      ctrl.verify();
    });

    it('a refresh that never answers is given up after ten seconds', async () => {
      vi.useFakeTimers();
      const started = auth.start();
      ctrl.expectOne('/auth/refresh');
      await vi.advanceTimersByTimeAsync(10_001);
      await started;
      expect(auth.token()).toBeNull();
      expect(auth.notice()).toBe('unreachable');
    });

    it('a token whose /auth/me cannot be read still counts as a session, with the bar', async () => {
      const started = auth.start();
      ctrl.expectOne('/auth/refresh').flush({ access_token: 'tok-1' });
      await settle();
      ctrl.expectOne('/auth/me').flush('oops', status(500));
      await started;
      expect(auth.token()).toBe('tok-1');
      expect(auth.me()).toBeNull();
      expect(auth.notice()).toBe('unreachable');
      ctrl.verify();
    });

    it('a /auth/me that never answers is given up after ten seconds too, with the bar', async () => {
      vi.useFakeTimers();
      const started = auth.start();
      ctrl.expectOne('/auth/refresh').flush({ access_token: 'tok-1' });
      await vi.advanceTimersByTimeAsync(1);
      ctrl.expectOne('/auth/me');
      await vi.advanceTimersByTimeAsync(10_001);
      await started;
      expect(auth.token()).toBe('tok-1');
      expect(auth.me()).toBeNull();
      expect(auth.notice()).toBe('unreachable');
    });

    it.each([
      ['a page of HTML', '<html>bad gateway</html>'],
      ['an object without roles', { sub: 's', email: 'a@b.example', org_id: 'o', org_name: 'Acme', permissions: [] }],
      ['roles that are not text', { sub: 's', email: 'a@b.example', org_id: 'o', org_name: 'Acme', roles: [1], permissions: [] }],
      ['null', null],
    ])('a 200 from /auth/me that is %s is not a person: no crash, no details, the bar', async (_name, body) => {
      const started = auth.start();
      ctrl.expectOne('/auth/refresh').flush({ access_token: 'tok-1' });
      await settle();
      ctrl.expectOne('/auth/me').flush(body);
      await started;
      expect(auth.me()).toBeNull();
      expect(auth.token()).toBe('tok-1');
      expect(auth.notice()).toBe('unreachable');
      ctrl.verify();
    });
  });

  describe('login', () => {
    it('keeps the token in memory and loads /auth/me', async () => {
      const done = auth.login('admin@example.test', 'pw');
      const request = ctrl.expectOne('/auth/login');
      expect(request.request.method).toBe('POST');
      expect(request.request.body).toEqual({ email: 'admin@example.test', password: 'pw' });
      request.flush({ status: 'authenticated', access_token: 'tok-9' });
      await settle();
      ctrl.expectOne('/auth/me').flush(adminMe);
      expect(await done).toEqual({ ok: true });
      expect(auth.token()).toBe('tok-9');
      expect(auth.me()).toEqual(adminMe);
      ctrl.verify();
    });

    it('never writes the token to localStorage, sessionStorage or a cookie', async () => {
      // Drives login itself, so that the token write of login is the one that is checked.
      const done = auth.login('admin@example.test', 'pw');
      const loginRequest = ctrl.expectOne('/auth/login');
      loginRequest.flush({ status: 'authenticated', access_token: 'secret-token-value' });
      await settle();
      const meRequest = ctrl.expectOne('/auth/me');
      meRequest.flush(adminMe);
      expect(await done).toEqual({ ok: true });
      expect(auth.token()).toBe('secret-token-value');
      // Nor in the address bar or in the address of any request the service made.
      expect(window.location.href).not.toContain('secret-token-value');
      expect(loginRequest.request.urlWithParams).not.toContain('secret-token-value');
      expect(meRequest.request.urlWithParams).not.toContain('secret-token-value');
      expect(JSON.stringify({ ...localStorage })).not.toContain('secret-token-value');
      expect(JSON.stringify({ ...sessionStorage })).not.toContain('secret-token-value');
      expect(document.cookie).not.toContain('secret-token-value');
      expect(localStorage.length + sessionStorage.length).toBe(0);
    });

    it.each([
      [401, { error: 'invalid_credentials' }, 'invalid_credentials'],
      [403, { error: 'email_not_verified' }, 'email_not_verified'],
      [403, { error: 'no_membership' }, 'no_membership'],
      [429, { error: 'too_many_attempts', retry_after_seconds: 90 }, 'too_many_attempts'],
      [400, { error: 'invalid_request' }, 'other'],
      [500, null, 'other'],
    ])('answer %s %j is the failure %s and leaves the person anonymous', async (code, body, kind) => {
      const done = auth.login('a@b.example', 'pw');
      ctrl.expectOne('/auth/login').flush(body, status(code));
      const result = await done;
      expect(result.ok).toBe(false);
      if (!result.ok) {
        expect(result.failure.kind).toBe(kind);
        if (result.failure.kind === 'too_many_attempts') {
          expect(result.failure.retryAfterSeconds).toBe(90);
        }
      }
      expect(auth.token()).toBeNull();
      ctrl.verify();
    });

    it('a 200 that is not "authenticated" is not a session', async () => {
      const done = auth.login('a@b.example', 'pw');
      ctrl.expectOne('/auth/login').flush({ status: 'mfa_required' });
      expect(await done).toEqual({ ok: false, failure: { kind: 'other' } });
      expect(auth.token()).toBeNull();
    });

    it('is signed in even when /auth/me cannot be read: the notes screen asks for the person again', async () => {
      const done = auth.login('a@b.example', 'pw');
      ctrl.expectOne('/auth/login').flush({ status: 'authenticated', access_token: 'tok-9' });
      await settle();
      ctrl.expectOne('/auth/me').flush('oops', status(500));
      expect(await done).toEqual({ ok: true });
      expect(auth.token()).toBe('tok-9');
      expect(auth.me()).toBeNull();
    });

    it('a /auth/me that never answers is given up after ten seconds: the sign-in does not hang', async () => {
      vi.useFakeTimers();
      const done = auth.login('a@b.example', 'pw');
      ctrl.expectOne('/auth/login').flush({ status: 'authenticated', access_token: 'tok-9' });
      await vi.advanceTimersByTimeAsync(1);
      ctrl.expectOne('/auth/me');
      await vi.advanceTimersByTimeAsync(10_001);
      expect(await done).toEqual({ ok: true });
      expect(auth.token()).toBe('tok-9');
      expect(auth.me()).toBeNull();
    });

    it('signing in as someone else while signed in: the old person is gone, even when /auth/me of the new one fails', async () => {
      await signedInAs(auth, ctrl, adminMe, 'tok-a');
      const done = auth.login('viewer@example.test', 'pw');
      ctrl.expectOne('/auth/login').flush({ status: 'authenticated', access_token: 'tok-b' });
      await settle();
      ctrl.expectOne('/auth/me').flush('oops', status(500));
      expect(await done).toEqual({ ok: true });
      expect(auth.token()).toBe('tok-b');
      expect(auth.me()).toBeNull();
      ctrl.verify();
    });

    it('signing in while the /auth/me of the old session is on its way: its late answer is not shown under the new person', async () => {
      await signedInAs(auth, ctrl, adminMe, 'tok-a');
      const late = auth.loadMe();
      const lateRequest = ctrl.expectOne('/auth/me');
      const done = auth.login('viewer@example.test', 'pw');
      ctrl.expectOne('/auth/login').flush({ status: 'authenticated', access_token: 'tok-b' });
      await settle();
      const newRequest = ctrl.expectOne('/auth/me');
      lateRequest.flush(adminMe);
      expect(await late).toBe(false);
      expect(auth.me()).toBeNull();
      newRequest.flush(viewerMe);
      expect(await done).toEqual({ ok: true });
      expect(auth.me()).toEqual(viewerMe);
      ctrl.verify();
    });

    it('a failed sign-in leaves the session that exists alone', async () => {
      await signedInAs(auth, ctrl, adminMe, 'tok-a');
      const done = auth.login('viewer@example.test', 'wrong');
      ctrl.expectOne('/auth/login').flush({ error: 'invalid_credentials' }, status(401));
      expect((await done).ok).toBe(false);
      expect(auth.token()).toBe('tok-a');
      expect(auth.me()).toEqual(adminMe);
    });

    it('clears an old notice when the person signs in', async () => {
      auth.showNotice('unreachable');
      const done = auth.login('a@b.example', 'pw');
      ctrl.expectOne('/auth/login').flush({ status: 'authenticated', access_token: 't' });
      await settle();
      ctrl.expectOne('/auth/me').flush(adminMe);
      await done;
      expect(auth.notice()).toBeNull();
    });
  });

  describe('logout', () => {
    it.each([
      ['204', () => ({ body: null, init: status(204) })],
      ['a 500', () => ({ body: 'oops', init: status(500) })],
    ])('drops the token and the person after %s', async (_name, answer) => {
      await signedInAs(auth, ctrl);
      const done = auth.logout();
      const request = ctrl.expectOne('/auth/logout');
      expect(request.request.method).toBe('POST');
      const { body, init } = answer();
      request.flush(body, init);
      await done;
      expect(auth.token()).toBeNull();
      expect(auth.me()).toBeNull();
    });

    it('drops the token when the network fails', async () => {
      await signedInAs(auth, ctrl);
      const done = auth.logout();
      ctrl.expectOne('/auth/logout').error(new ProgressEvent('error'));
      await done;
      expect(auth.token()).toBeNull();
      expect(auth.me()).toBeNull();
    });
  });

  describe('refresh', () => {
    it('one request for any number of callers that ask while it runs', async () => {
      const first = auth.refresh();
      const second = auth.refresh();
      const third = auth.refresh();
      ctrl.expectOne('/auth/refresh').flush({ access_token: 'new' });
      expect(await Promise.all([first, second, third])).toEqual(['ok', 'ok', 'ok']);
      expect(auth.token()).toBe('new');
      const later = auth.refresh();
      ctrl.expectOne('/auth/refresh').flush({ access_token: 'newer' });
      await later;
      expect(auth.token()).toBe('newer');
    });

    it('401 is rejected: the token is dropped', async () => {
      await holdToken(auth, ctrl, 'old');
      const done = auth.refresh();
      ctrl.expectOne('/auth/refresh').flush({ error: 'invalid_grant' }, status(401));
      expect(await done).toBe('rejected');
      expect(auth.token()).toBeNull();
    });

    it('any other failure is unavailable: the token is kept', async () => {
      await holdToken(auth, ctrl, 'old');
      const done = auth.refresh();
      ctrl.expectOne('/auth/refresh').flush('oops', status(503));
      expect(await done).toBe('unavailable');
      expect(auth.token()).toBe('old');
    });

    it('an answer that was on its way when the person signed out does not bring the session back', async () => {
      await signedInAs(auth, ctrl, adminMe, 'old');
      const refreshing = auth.refresh();
      const refreshRequest = ctrl.expectOne('/auth/refresh');
      const loading = auth.loadMe();
      const meRequest = ctrl.expectOne('/auth/me');
      const signingOut = auth.logout();
      ctrl.expectOne('/auth/logout').flush(null, status(204));
      await signingOut;
      refreshRequest.flush({ access_token: 'late' });
      meRequest.flush(adminMe);
      expect(await refreshing).toBe('rejected');
      expect(await loading).toBe(false);
      expect(auth.token()).toBeNull();
      expect(auth.me()).toBeNull();
    });
  });

  describe('loadMe', () => {
    it('one request for any number of callers that ask while it runs, then a new one', async () => {
      await holdToken(auth, ctrl, 'tok');
      const calls = [auth.loadMe(), auth.loadMe(), auth.loadMe()];
      ctrl.expectOne('/auth/me').flush(adminMe);
      expect(await Promise.all(calls)).toEqual([true, true, true]);
      expect(auth.me()).toEqual(adminMe);
      const later = auth.loadMe();
      ctrl.expectOne('/auth/me').flush(viewerMe);
      expect(await later).toBe(true);
      expect(auth.me()).toEqual(viewerMe);
    });

    it('a call after the person signed out and in again does not share the answer that was on its way', async () => {
      await signedInAs(auth, ctrl, adminMe, 'old');
      const stale = auth.loadMe();
      const staleRequest = ctrl.expectOne('/auth/me');
      auth.dropSession();
      await holdToken(auth, ctrl, 'new');
      const fresh = auth.loadMe();
      const freshRequest = ctrl.expectOne('/auth/me');
      staleRequest.flush(adminMe);
      freshRequest.flush(viewerMe);
      expect(await stale).toBe(false);
      expect(await fresh).toBe(true);
      expect(auth.me()).toEqual(viewerMe);
    });

    it('a failed call does not stop the next one', async () => {
      await holdToken(auth, ctrl, 'tok');
      const failed = auth.loadMe();
      ctrl.expectOne('/auth/me').flush('oops', status(500));
      expect(await failed).toBe(false);
      const again = auth.loadMe();
      ctrl.expectOne('/auth/me').flush(adminMe);
      expect(await again).toBe(true);
    });
  });

  describe('a refresh that was on its way when the person signed out and in again', () => {
    it('401 does not drop the new session', async () => {
      await signedInAs(auth, ctrl, adminMe, 'old');
      const refreshing = auth.refresh();
      const refreshRequest = ctrl.expectOne('/auth/refresh');
      const signingOut = auth.logout();
      ctrl.expectOne('/auth/logout').flush(null, status(204));
      await signingOut;
      const signingIn = auth.login('admin@example.test', 'pw');
      ctrl.expectOne('/auth/login').flush({ status: 'authenticated', access_token: 'new' });
      await settle();
      ctrl.expectOne('/auth/me').flush(adminMe);
      await signingIn;
      refreshRequest.flush({ error: 'invalid_grant' }, status(401));
      expect(await refreshing).toBe('rejected');
      expect(auth.token()).toBe('new');
      expect(auth.me()).toEqual(adminMe);
    });
  });

  describe('notices', () => {
    it('can be shown and cleared', () => {
      expect(auth.notice()).toBeNull();
      auth.showNotice('tryLater');
      expect(auth.notice()).toBe('tryLater');
      auth.clearNotice();
      expect(auth.notice()).toBeNull();
    });
  });
});

describe('failureOf', () => {
  const failure = (code: number, body: unknown) =>
    failureOf(new HttpErrorResponse({ status: code, error: body }));

  it('reads the rules of a weak password and ignores what is not a rule name', () => {
    expect(failure(400, { error: 'weak_password', rules: ['too_short', 7, 'requires_digit'] })).toEqual({
      kind: 'weak_password',
      rules: ['too_short', 'requires_digit'],
    });
  });

  it('reads invalid_token and already_member', () => {
    expect(failure(400, { error: 'invalid_token' })).toEqual({ kind: 'invalid_token' });
    expect(failure(409, { error: 'already_member' })).toEqual({ kind: 'already_member' });
  });

  it('takes a minute when a 429 does not say how long to wait', () => {
    expect(failure(429, { error: 'too_many_attempts' })).toEqual({ kind: 'too_many_attempts', retryAfterSeconds: 60 });
  });

  it('is "other" for what it does not know: another status, a body that is not JSON, no HTTP error at all', () => {
    expect(failure(500, { error: 'invalid_token' })).toEqual({ kind: 'other' });
    expect(failure(400, '<html>bad request</html>')).toEqual({ kind: 'other' });
    expect(failure(0, new ProgressEvent('error'))).toEqual({ kind: 'other' });
    expect(failureOf(new Error('boom'))).toEqual({ kind: 'other' });
  });
});
