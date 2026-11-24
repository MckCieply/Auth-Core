import { HttpClient, HttpErrorResponse, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { lastValueFrom } from 'rxjs';
import { vi } from 'vitest';
import { privateMe, settle, signedInAs } from '../../testing/helpers';
import { authInterceptor } from './auth.interceptor';
import { AuthService, failureOf } from './auth.service';

@Component({ template: '' })
class Blank {}

const status = (code: number) => ({ status: code, statusText: String(code) });

function setUp() {
  TestBed.configureTestingModule({
    providers: [
      provideRouter([
        { path: 'settings', component: Blank },
        { path: 'login', component: Blank },
      ]),
      provideHttpClient(withInterceptors([authInterceptor])),
      provideHttpClientTesting(),
    ],
  });
  return { auth: TestBed.inject(AuthService), ctrl: TestBed.inject(HttpTestingController), http: TestBed.inject(HttpClient), router: TestBed.inject(Router) };
}

/** Answers every GET /auth/me and GET /auth/orgs that is open, and those the answers start, until none is left. */
async function answerMe(ctrl: HttpTestingController): Promise<void> {
  for (let round = 0; round < 5; round += 1) {
    await settle();
    ctrl.match('/auth/me').forEach((request) => request.flush(privateMe));
    ctrl.match('/auth/orgs').forEach((request) => request.flush({ orgs: [] }));
  }
  await settle();
}

describe('AuthService: the settings calls (spec 0012)', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it.each([
    ['/auth/account/confirm', 'confirmPassword', ['pw'], { password: 'pw' }],
    ['/auth/account/confirm', 'confirmWithTicket', ['T'], { provider_ticket: 'T' }],
    ['/auth/providers/connect', 'connect', ['T'], { ticket: 'T' }],
  ] as const)('%s: %s sends its body with the token, then reads the person again', async (url, method, args, body) => {
    const { auth, ctrl } = setUp();
    await signedInAs(auth, ctrl, privateMe, 'tok');
    const done = (auth[method] as (...a: string[]) => Promise<unknown>)(...args);
    const request = ctrl.expectOne(url);
    expect(request.request.body).toEqual(body);
    expect(request.request.headers.get('Authorization')).toBe('Bearer tok');
    request.flush(null, status(204));
    await settle();
    ctrl.expectOne('/auth/me').flush({ ...privateMe, confirmed_until: '2026-05-04T18:35:00Z' });
    expect(await done).toEqual({ ok: true });
    expect(auth.me()?.confirmed_until).toBe('2026-05-04T18:35:00Z');
  });

  it.each([
    ['unlink', ['google'], '/auth/account/logins/unlink', { provider: 'google' }],
    ['changePassword', ['Old-Passw0rd', 'New-Passw0rd'], '/auth/account/password', { current_password: 'Old-Passw0rd', new_password: 'New-Passw0rd' }],
  ] as const)('%s stores the new access token of the answer and keeps the person signed in', async (method, args, url, body) => {
    const { auth, ctrl } = setUp();
    await signedInAs(auth, ctrl, privateMe, 'old');
    const session = auth.session;
    const done = (auth[method] as (...a: string[]) => Promise<unknown>)(...args);
    const request = ctrl.expectOne(url);
    expect(request.request.body).toEqual(body);
    request.flush({ status: 'authenticated', access_token: 'renewed' });
    await settle();
    ctrl.expectOne('/auth/me').flush(privateMe);
    expect(await done).toEqual({ ok: true });
    expect(auth.token()).toBe('renewed');
    expect(auth.session).toBe(session);   // the same session in this tab: nothing the person was doing is dropped
  });

  it.each([
    ['unlink', ['google'], '/auth/account/logins/unlink'],
    ['changePassword', ['Old-Passw0rd', 'New-Passw0rd'], '/auth/account/password'],
  ] as const)('while %s is in flight no other request to /auth/ leaves: the refresh and another settings call wait for it', async (method, args, url) => {
    const { auth, ctrl, http } = setUp();
    await signedInAs(auth, ctrl, privateMe, 'old');
    const renewal = (auth[method] as (...a: string[]) => Promise<unknown>)(...args);
    await settle();
    const refresh = auth.refresh();
    const other = lastValueFrom(http.post('/auth/account/confirm', { password: 'x' }));
    const notes = lastValueFrom(http.get('/api/notes'));   // not under /auth: it is not held
    ctrl.expectOne('/api/notes').flush([]);
    await notes;
    ctrl.expectNone('/auth/refresh');
    ctrl.expectNone('/auth/account/confirm');
    ctrl.expectOne(url).flush({ status: 'authenticated', access_token: 'renewed' });
    await settle();
    const confirm = ctrl.expectOne('/auth/account/confirm');
    expect(confirm.request.headers.get('Authorization')).toBe('Bearer renewed');
    confirm.flush(null, status(204));
    ctrl.expectOne('/auth/refresh').flush({ access_token: 'later' });
    await answerMe(ctrl);
    await Promise.all([renewal, refresh, other]);
  });

  it('a refused renewal keeps the old token and lets the held requests go', async () => {
    const { auth, ctrl } = setUp();
    await signedInAs(auth, ctrl, privateMe, 'old');
    const unlink = auth.unlink('google');
    await settle();
    const refresh = auth.refresh();
    ctrl.expectOne('/auth/account/logins/unlink').flush({ error: 'last_login' }, status(409));
    expect(await unlink).toEqual({ ok: false, failure: { kind: 'last_login' } });
    await settle();
    ctrl.expectOne('/auth/refresh').flush({ access_token: 'old2' });
    await refresh;
    await answerMe(ctrl);
    expect(auth.token()).toBe('old2');
  });

  it.each([
    ['unlink', ['google'], '/auth/account/logins/unlink'],
    ['changePassword', ['Old-Passw0rd', 'New-Passw0rd'], '/auth/account/password'],
  ] as const)('an expired access token during %s: the refresh its 401 starts is not held behind it, and the call goes through with no timeout', async (method, args, url) => {
    const { auth, ctrl } = setUp();
    await signedInAs(auth, ctrl, privateMe, 'old');
    const done = (auth[method] as (...a: string[]) => Promise<unknown>)(...args);
    await settle();
    ctrl.expectOne(url).flush(null, status(401));   // the plain 401 of an expired token
    await settle();
    ctrl.expectOne('/auth/refresh').flush({ access_token: 'fresh' });   // sent at once: the call waits for it, it does not wait for the call
    await settle();
    const again = ctrl.expectOne(url);
    expect(again.request.headers.get('Authorization')).toBe('Bearer fresh');
    again.flush({ status: 'authenticated', access_token: 'renewed' });
    await answerMe(ctrl);   // the renewal's /auth/me, and the one the refresh's new token asks for
    expect(await done).toEqual({ ok: true });
    expect(auth.token()).toBe('renewed');
  });

  it('a plain refresh() started during a renewal does not hold up the refresh that the renewal\'s own 401 starts', async () => {
    const { auth, ctrl } = setUp();
    await signedInAs(auth, ctrl, privateMe, 'old');
    const unlink = auth.unlink('google');
    await settle();
    const plain = auth.refresh();   // its request is held behind the renewal; its promise is out
    ctrl.expectNone('/auth/refresh');
    ctrl.expectOne('/auth/account/logins/unlink').flush(null, status(401));   // an expired access token
    await settle();
    ctrl.expectOne('/auth/refresh').flush({ access_token: 'fresh' });   // the renewal's own refresh: not the plain one's promise, so not waiting for it
    await settle();
    ctrl.expectOne('/auth/account/logins/unlink').flush({ status: 'authenticated', access_token: 'renewed' });
    await answerMe(ctrl);
    expect(await unlink).toEqual({ ok: true });
    ctrl.expectOne('/auth/refresh').flush({ access_token: 'later' });   // the held plain refresh goes out once the renewal is over
    expect(await plain).toBe('ok');
    await answerMe(ctrl);
  });

  it('401 session_required signs the person out to /login with the notice and never starts a refresh', async () => {
    const { auth, ctrl, router } = setUp();
    await signedInAs(auth, ctrl, privateMe, 'tok');
    const navigate = vi.spyOn(router, 'navigate');
    const done = auth.confirmPassword('pw');
    ctrl.expectOne('/auth/account/confirm').flush({ error: 'session_required' }, status(401));
    expect(await done).toEqual({ ok: false, failure: { kind: 'session_required' } });
    ctrl.expectNone('/auth/refresh');
    expect(auth.token()).toBeNull();
    expect(navigate).toHaveBeenCalledWith(['/login'], { state: { notice: 'session' } });
  });

  it('a settings call asked while a switch is on its way waits for it and is sent with the switched token', async () => {
    const { auth, ctrl } = setUp();
    await signedInAs(auth, ctrl, privateMe, 'old');
    const switched = auth.switchTo('org-b');
    const change = auth.changePassword('Old-Passw0rd', 'New-Passw0rd');
    const unlink = auth.unlink('github');
    await settle();
    ctrl.expectNone('/auth/account/password');
    ctrl.expectNone('/auth/account/logins/unlink');
    ctrl.expectOne('/auth/switch').flush({ access_token: 'switched' });
    await settle();
    const password = ctrl.expectOne('/auth/account/password');
    expect(password.request.headers.get('Authorization')).toBe('Bearer switched');
    ctrl.expectNone('/auth/account/logins/unlink');   // one renewal at a time: the second waits for the first answer
    password.flush({ status: 'authenticated', access_token: 'renewed-1' });
    await settle();
    const link = ctrl.expectOne('/auth/account/logins/unlink');
    expect(link.request.headers.get('Authorization')).toBe('Bearer renewed-1');
    link.flush({ status: 'authenticated', access_token: 'renewed-2' });
    await answerMe(ctrl);
    expect(await switched).toBe('switched');
    expect(await change).toEqual({ ok: true });
    expect(await unlink).toEqual({ ok: true });
    expect(auth.token()).toBe('renewed-2');
  });

  it('a switch asked during a renewal is sent after the renewal answered', async () => {
    const { auth, ctrl } = setUp();
    await signedInAs(auth, ctrl, privateMe, 'old');
    const unlink = auth.unlink('google');
    await settle();
    const switched = auth.switchTo('org-b');
    await settle();
    ctrl.expectNone('/auth/switch');
    ctrl.expectOne('/auth/account/logins/unlink').flush({ status: 'authenticated', access_token: 'renewed' });
    await settle();
    ctrl.expectOne('/auth/switch').flush({ access_token: 'switched' });
    await answerMe(ctrl);
    expect(await unlink).toEqual({ ok: true });
    expect(await switched).toBe('switched');
    expect(auth.token()).toBe('switched');
  });

  it('an expired token on the request of the renewal while a switch waits: the refresh goes at once, then the call, then the switch', async () => {
    const { auth, ctrl } = setUp();
    await signedInAs(auth, ctrl, privateMe, 'old');
    const unlink = auth.unlink('google');
    await settle();
    const switched = auth.switchTo('org-b');   // its request is held behind the renewal
    await settle();
    ctrl.expectOne('/auth/account/logins/unlink').flush(null, status(401));   // an expired access token
    await settle();
    ctrl.expectOne('/auth/refresh').flush({ access_token: 'fresh' });   // not waiting for the switch, which waits for the renewal
    await settle();
    ctrl.expectNone('/auth/switch');
    const again = ctrl.expectOne('/auth/account/logins/unlink');
    expect(again.request.headers.get('Authorization')).toBe('Bearer fresh');
    again.flush({ status: 'authenticated', access_token: 'renewed' });
    await settle();
    ctrl.expectOne('/auth/switch').flush({ access_token: 'switched' });
    await answerMe(ctrl);
    expect(await unlink).toEqual({ ok: true });
    expect(await switched).toBe('switched');
    expect(auth.token()).toBe('switched');
  });

  it('a renewal asked while a refresh is on its way waits for it', async () => {
    const { auth, ctrl } = setUp();
    await signedInAs(auth, ctrl, privateMe, 'old');
    const refresh = auth.refresh();
    const unlink = auth.unlink('google');
    await settle();
    ctrl.expectNone('/auth/account/logins/unlink');
    ctrl.expectOne('/auth/refresh').flush({ access_token: 'fresh' });
    expect(await refresh).toBe('ok');
    await answerMe(ctrl);
    const request = ctrl.expectOne('/auth/account/logins/unlink');
    expect(request.request.headers.get('Authorization')).toBe('Bearer fresh');
    request.flush({ status: 'authenticated', access_token: 'renewed' });
    await answerMe(ctrl);
    expect(await unlink).toEqual({ ok: true });
  });

  it('a second renewal asked while one is in flight is sent after the first answered', async () => {
    const { auth, ctrl } = setUp();
    await signedInAs(auth, ctrl, privateMe, 'old');
    const first = auth.unlink('google');
    const second = auth.changePassword('Old-Passw0rd', 'New-Passw0rd');
    await settle();
    ctrl.expectNone('/auth/account/password');
    ctrl.expectOne('/auth/account/logins/unlink').flush({ status: 'authenticated', access_token: 'renewed-1' });
    await settle();
    const request = ctrl.expectOne('/auth/account/password');
    expect(request.request.headers.get('Authorization')).toBe('Bearer renewed-1');
    request.flush({ status: 'authenticated', access_token: 'renewed-2' });
    await answerMe(ctrl);
    expect(await first).toEqual({ ok: true });
    expect(await second).toEqual({ ok: true });
    expect(auth.token()).toBe('renewed-2');
  });
});

// Its own describe: no TestBed is configured here, so the HTTP controller of the describe above is not asked for.
describe('failureOf: the answers of the settings endpoints (spec 0012)', () => {
  it.each([
    [401, 'session_required', { kind: 'session_required' }],
    [403, 'confirmation_required', { kind: 'confirmation_required' }],
    [403, 'password_not_set', { kind: 'password_not_set' }],
    [409, 'provider_in_use', { kind: 'provider_in_use' }],
    [409, 'last_login', { kind: 'last_login' }],
    [404, 'not_found', { kind: 'not_found' }],
    [403, 'last_login', { kind: 'other' }],
    [409, 'confirmation_required', { kind: 'other' }],
  ])('%i %s reads as %j', (code, error, expected) => {
    expect(failureOf(new HttpErrorResponse({ status: code, error: { error } }))).toEqual(expected);
  });
});
