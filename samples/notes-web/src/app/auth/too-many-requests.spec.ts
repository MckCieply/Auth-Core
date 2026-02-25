import { Component } from '@angular/core';
import { HttpClient, HttpErrorResponse, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { lastValueFrom } from 'rxjs';
import { holdToken, settle } from '../../testing/helpers';
import { texts } from '../texts';
import { authInterceptor } from './auth.interceptor';
import { AuthService, failureOf, retryAfterOf } from './auth.service';

@Component({ template: '' })
class Blank {}

const status = (code: number) => ({ status: code, statusText: String(code) });
const tooMany = (seconds: unknown = 12) => ({ error: 'too_many_requests', retry_after_seconds: seconds });
const outage = { error: 'temporarily_unavailable' };

describe('a 429 too_many_requests', () => {
  let http: HttpClient;
  let ctrl: HttpTestingController;
  let auth: AuthService;
  let router: Router;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: 'notes', component: Blank },
          { path: 'login', component: Blank },
        ]),
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpClient);
    ctrl = TestBed.inject(HttpTestingController);
    auth = TestBed.inject(AuthService);
    router = TestBed.inject(Router);
    await router.navigateByUrl('/notes');
    await holdToken(auth, ctrl, 'tok');
  });

  afterEach(() => ctrl.verify());

  it('says "Try again in N s." and has the notice bar show it', () => {
    expect(texts.notices.tryAgainIn(12)).toBe('Try again in 12 s.');
    expect(texts.notices.tryAgainIn(1)).toBe('Try again in 1 s.');
  });

  it('from an endpoint that needs no token: the notice, and the session stays', async () => {
    const done = lastValueFrom(http.post('/auth/login', { email: 'a@b.example', password: 'x' })).catch((error: unknown) => error);
    ctrl.expectOne('/auth/login').flush(tooMany(12), status(429));
    expect(await done).toBeInstanceOf(HttpErrorResponse);
    expect(auth.notice()).toBe('wait');
    expect(auth.noticeSeconds()).toBe(12);
    expect(auth.token()).toBe('tok');
    expect(router.url).toBe('/notes');
  });

  it.each(['/api/notes', '/auth/me', '/auth/org/members'])('from %s: the notice, no refresh, nobody signed out', async (url) => {
    const done = lastValueFrom(http.get(url)).catch((error: unknown) => error);
    ctrl.expectOne(url).flush(tooMany(30), status(429));
    expect(await done).toBeInstanceOf(HttpErrorResponse);
    await settle();
    ctrl.expectNone('/auth/refresh');
    expect(auth.notice()).toBe('wait');
    expect(auth.noticeSeconds()).toBe(30);
    expect(auth.token()).toBe('tok');
    expect(router.url).toBe('/notes');
  });

  it.each([
    ['no number', { error: 'too_many_requests' }],
    ['a number that is not one', tooMany('soon')],
    ['zero', tooMany(0)],
    ['a negative number', tooMany(-5)],
  ])('with %s the wait is a minute', async (_name, body) => {
    const done = lastValueFrom(http.post('/auth/password/forgot', { email: 'a@b.example' })).catch((error: unknown) => error);
    ctrl.expectOne('/auth/password/forgot').flush(body, status(429));
    await done;
    expect(auth.notice()).toBe('wait');
    expect(auth.noticeSeconds()).toBe(60);
  });

  it.each([
    ['the lockout of a person (too_many_attempts), which the login screen words itself', { error: 'too_many_attempts', retry_after_seconds: 120 }, 429],
    ['a 429 with no code (a proxy)', '<html>slow down</html>', 429],
    ['a 503 of the service', outage, 503],
    ['a 500', { error: 'internal_error' }, 500],
  ])('is not announced: %s', async (_name, body, code) => {
    const done = lastValueFrom(http.post('/auth/login', {})).catch((error: unknown) => error);
    ctrl.expectOne('/auth/login').flush(body, status(code));
    await done;
    expect(auth.notice()).toBeNull();
    expect(auth.token()).toBe('tok');
  });

  it('from another origin: not announced', async () => {
    const done = lastValueFrom(http.get('https://elsewhere.example/api')).catch((error: unknown) => error);
    ctrl.expectOne('https://elsewhere.example/api').flush(tooMany(9), status(429));
    await done;
    expect(auth.notice()).toBeNull();
  });

  it('a refresh answered 429 keeps the session and says when to try again', async () => {
    const refreshed = auth.refresh();
    ctrl.expectOne('/auth/refresh').flush(tooMany(30), status(429));
    expect(await refreshed).toBe('limited');
    expect(auth.token()).toBe('tok');
    expect(auth.notice()).toBe('wait');
    expect(auth.noticeSeconds()).toBe(30);
  });

  it('the start of the app: a refresh answered 429 shows "wait", one answered 503 shows "unreachable"', async () => {
    const limited = auth.start();
    ctrl.expectOne('/auth/refresh').flush(tooMany(20), status(429));
    await limited;
    expect(auth.notice()).toBe('wait');
    expect(auth.noticeSeconds()).toBe(20);

    auth.clearNotice();
    const down = auth.start();
    ctrl.expectOne('/auth/refresh').flush(outage, status(503));
    await down;
    expect(auth.notice()).toBe('unreachable');
    expect(auth.token()).toBe('tok');
  });

  it('an old "wait" notice does not hide a later outage: a refresh answered 503 says "unreachable"', async () => {
    auth.showNotice('wait', 5);
    const done = lastValueFrom(http.get('/api/notes')).catch((error: unknown) => error);
    ctrl.expectOne('/api/notes').flush(null, status(401));
    await settle();
    ctrl.expectOne('/auth/refresh').flush(outage, status(503));
    await settle();
    expect(await done).toBeInstanceOf(HttpErrorResponse);
    expect(auth.notice()).toBe('unreachable');
    expect(auth.token()).toBe('tok');
  });

  it('a refresh answered 503 temporarily_unavailable keeps the session', async () => {
    const refreshed = auth.refresh();
    ctrl.expectOne('/auth/refresh').flush(outage, status(503));
    expect(await refreshed).toBe('unavailable');
    expect(auth.token()).toBe('tok');
    expect(router.url).toBe('/notes');
  });

  it('a request that got a 401, whose refresh then gets 429, keeps the session and the "try again" notice', async () => {
    const done = lastValueFrom(http.get('/api/notes')).catch((error: unknown) => error);
    ctrl.expectOne('/api/notes').flush(null, status(401));
    await settle();
    ctrl.expectOne('/auth/refresh').flush(tooMany(8), status(429));
    await settle();
    expect(await done).toBeInstanceOf(HttpErrorResponse);
    expect(auth.token()).toBe('tok');
    expect(auth.notice()).toBe('wait');   // not replaced by "can't reach the server"
    expect(auth.noticeSeconds()).toBe(8);
    expect(router.url).toBe('/notes');   // no trip to /login
  });

  it('a request that got a 401, whose refresh then gets 503, keeps the session too', async () => {
    const done = lastValueFrom(http.get('/api/notes')).catch((error: unknown) => error);
    ctrl.expectOne('/api/notes').flush(null, status(401));
    await settle();
    ctrl.expectOne('/auth/refresh').flush(outage, status(503));
    await settle();
    expect(await done).toBeInstanceOf(HttpErrorResponse);
    expect(auth.token()).toBe('tok');
    expect(router.url).toBe('/notes');
  });
});

describe('the answers of the service that failureOf and retryAfterOf read', () => {
  const answer = (code: number, body: unknown) => new HttpErrorResponse({ status: code, error: body });

  it('a 429 too_many_requests is its own failure, with the wait', () => {
    expect(failureOf(answer(429, tooMany(7)))).toEqual({ kind: 'too_many_requests', retryAfterSeconds: 7 });
    expect(failureOf(answer(429, tooMany('x')))).toEqual({ kind: 'too_many_requests', retryAfterSeconds: 60 });
  });

  it('a 429 too_many_attempts is still the lockout, its wait read the same way', () => {
    expect(failureOf(answer(429, { error: 'too_many_attempts', retry_after_seconds: 90 }))).toEqual({
      kind: 'too_many_attempts',
      retryAfterSeconds: 90,
    });
    expect(failureOf(answer(429, { error: 'too_many_attempts', retry_after_seconds: 89.2 }))).toEqual({
      kind: 'too_many_attempts',
      retryAfterSeconds: 90,
    });
  });

  it('retryAfterOf is the number of the body, a minute when there is none', () => {
    expect(retryAfterOf(answer(429, tooMany(5)))).toBe(5);
    expect(retryAfterOf(answer(429, tooMany(2.5)))).toBe(3);
    expect(retryAfterOf(answer(429, tooMany(Infinity)))).toBe(60);
    expect(retryAfterOf(answer(429, null))).toBe(60);
    expect(retryAfterOf(answer(429, 'text'))).toBe(60);
  });
});
