import { Component } from '@angular/core';
import { HttpClient, HttpErrorResponse, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { lastValueFrom } from 'rxjs';
import { vi } from 'vitest';
import { adminMe, holdToken, settle } from '../../testing/helpers';
import { authInterceptor, wantsToken } from './auth.interceptor';
import { AuthService } from './auth.service';

@Component({ template: '' })
class Blank {}

const status = (code: number) => ({ status: code, statusText: String(code) });

describe('wantsToken', () => {
  const origin = 'https://app.example';

  it.each(['/api/notes', '/api/', '/auth/me', '/auth/org', '/auth/org/members', 'https://app.example/api/notes'])(
    'is true for %s',
    (url) => expect(wantsToken(url, origin)).toBe(true),
  );

  it.each([
    '/auth/login',
    '/auth/refresh',
    '/auth/logout',
    '/auth/password/forgot',
    '/auth/me/other',
    '/api',
    '/apiary',
    '/notes',
    'https://evil.example/api/notes',
    '//evil.example/api/notes',
    'https://app.example.evil.example/api/notes',
    '/api/../auth/login',
    'not a url at all %',
  ])('is false for %s', (url) => expect(wantsToken(url, origin)).toBe(false));
});

describe('authInterceptor', () => {
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
    await holdToken(auth, ctrl, 'old');
  });

  afterEach(() => ctrl.verify());

  describe('which requests get the token', () => {
    it.each(['/api/notes', '/auth/me', '/auth/org', '/auth/org/members'])('sends it to %s', async (url) => {
      const done = lastValueFrom(http.get(url));
      const request = ctrl.expectOne(url);
      expect(request.request.headers.get('Authorization')).toBe('Bearer old');
      request.flush({});
      await done;
    });

    it('sends it to the app origin when the URL is absolute', async () => {
      const url = `${window.location.origin}/api/notes`;
      const done = lastValueFrom(http.get(url));
      const request = ctrl.expectOne(url);
      expect(request.request.headers.get('Authorization')).toBe('Bearer old');
      request.flush({});
      await done;
    });

    it.each([
      '/auth/login',
      '/auth/refresh',
      '/auth/logout',
      '/auth/password/forgot',
      '/auth/invites/accept',
      'https://evil.example/api/notes',
      '//evil.example/api/notes',
      '/api/../auth/login',
    ])('does not send it to %s', async (url) => {
      const done = lastValueFrom(http.get(url));
      const request = ctrl.expectOne(url);
      expect(request.request.headers.has('Authorization')).toBe(false);
      request.flush({});
      await done;
    });
  });

  describe('401', () => {
    it('renews the token once and sends the request again with it', async () => {
      const done = lastValueFrom(http.get('/api/notes'));
      ctrl.expectOne('/api/notes').flush(null, status(401));
      await settle();
      ctrl.expectOne('/auth/refresh').flush({ access_token: 'new' });
      await settle();
      const again = ctrl.expectOne('/api/notes');
      expect(again.request.headers.get('Authorization')).toBe('Bearer new');
      again.flush([{ id: '1' }]);
      expect(await done).toEqual([{ id: '1' }]);
      expect(auth.token()).toBe('new');
    });

    it('one refresh for several 401s at once, then every request again', async () => {
      const urls = ['/api/a', '/api/b', '/api/c'];
      const calls = urls.map((url) => lastValueFrom(http.get(url)));
      const first = urls.map((url) => ctrl.expectOne(url));
      first.forEach((request) => request.flush(null, status(401)));
      await settle();
      ctrl.expectOne('/auth/refresh').flush({ access_token: 'new' });
      await settle();
      for (const url of urls) {
        const again = ctrl.expectOne(url);
        expect(again.request.headers.get('Authorization')).toBe('Bearer new');
        again.flush(url);
      }
      expect(await Promise.all(calls)).toEqual(urls);
    });

    it('a 401 that arrives after another request renewed the token is sent again without a refresh', async () => {
      const a = lastValueFrom(http.get('/api/a'));
      const b = lastValueFrom(http.get('/api/b'));
      const firstA = ctrl.expectOne('/api/a');
      const firstB = ctrl.expectOne('/api/b');
      firstA.flush(null, status(401));
      await settle();
      ctrl.expectOne('/auth/refresh').flush({ access_token: 'new' });
      await settle();
      ctrl.expectOne('/api/a').flush('a');
      firstB.flush(null, status(401));
      await settle();
      const againB = ctrl.expectOne('/api/b');
      expect(againB.request.headers.get('Authorization')).toBe('Bearer new');
      againB.flush('b');
      expect(await Promise.all([a, b])).toEqual(['a', 'b']);
      ctrl.expectNone('/auth/refresh');
    });

    it('401 from the refresh: the token is dropped and the person goes to /login with the page they were on', async () => {
      const done = lastValueFrom(http.get('/api/notes')).catch((error: unknown) => error);
      ctrl.expectOne('/api/notes').flush(null, status(401));
      await settle();
      ctrl.expectOne('/auth/refresh').flush({ error: 'invalid_grant' }, status(401));
      const error = await done;
      expect(error).toBeInstanceOf(HttpErrorResponse);
      expect((error as HttpErrorResponse).status).toBe(401);
      expect(auth.token()).toBeNull();
      await vi.waitFor(() => expect(router.url).toBe('/login?returnUrl=%2Fnotes'));
    });

    it('does not send a second request, and no second refresh, when the retried request gets a 401', async () => {
      const done = lastValueFrom(http.get('/api/notes')).catch((error: unknown) => error);
      ctrl.expectOne('/api/notes').flush(null, status(401));
      await settle();
      ctrl.expectOne('/auth/refresh').flush({ access_token: 'new' });
      await settle();
      ctrl.expectOne('/api/notes').flush(null, status(401));
      const error = await done;
      expect((error as HttpErrorResponse).status).toBe(401);
      await settle();
      ctrl.expectNone('/api/notes');
      ctrl.expectNone('/auth/refresh');
      expect(auth.token()).toBe('new');
    });

    it('a refresh that fails for another reason shows the bar, keeps the token and hands over the original answer', async () => {
      const done = lastValueFrom(http.get('/api/notes')).catch((error: unknown) => error);
      ctrl.expectOne('/api/notes').flush(null, status(401));
      await settle();
      ctrl.expectOne('/auth/refresh').flush('oops', status(502));
      const error = await done;
      expect((error as HttpErrorResponse).status).toBe(401);
      expect(auth.notice()).toBe('unreachable');
      expect(auth.token()).toBe('old');
      expect(router.url).toBe('/notes');
    });

    it('does nothing about a 401 when the session already ended', async () => {
      auth.dropSession();
      const done = lastValueFrom(http.get('/api/notes')).catch((error: unknown) => error);
      ctrl.expectOne('/api/notes').flush(null, status(401));
      const error = await done;
      expect((error as HttpErrorResponse).status).toBe(401);
      await settle();
      ctrl.expectNone('/auth/refresh');
    });
  });

  describe('403', () => {
    it('forbidden: the message, no refresh, the same token', async () => {
      const done = lastValueFrom(http.get('/api/notes')).catch((error: unknown) => error);
      ctrl.expectOne('/api/notes').flush({ error: 'forbidden' }, status(403));
      const error = await done;
      expect((error as HttpErrorResponse).status).toBe(403);
      await settle();
      ctrl.expectNone('/auth/refresh');
      expect(auth.notice()).toBe('forbidden');
      expect(auth.token()).toBe('old');
    });

    it('permissions_changed: one refresh, the request again once, and the person asked for again', async () => {
      const done = lastValueFrom(http.get('/api/notes'));
      ctrl.expectOne('/api/notes').flush({ error: 'permissions_changed' }, status(403));
      await settle();
      ctrl.expectOne('/auth/refresh').flush({ access_token: 'new' });
      await settle();
      ctrl.expectOne('/auth/me').flush(adminMe);
      const again = ctrl.expectOne('/api/notes');
      expect(again.request.headers.get('Authorization')).toBe('Bearer new');
      again.flush([]);
      expect(await done).toEqual([]);
      await settle();
      expect(auth.me()).toEqual(adminMe);
      expect(auth.notice()).toBeNull();
    });

    it('permissions_changed on the retried request: no second refresh, no third request', async () => {
      const done = lastValueFrom(http.get('/api/notes')).catch((error: unknown) => error);
      ctrl.expectOne('/api/notes').flush({ error: 'permissions_changed' }, status(403));
      await settle();
      ctrl.expectOne('/auth/refresh').flush({ access_token: 'new' });
      await settle();
      ctrl.expectOne('/auth/me').flush(adminMe);
      ctrl.expectOne('/api/notes').flush({ error: 'permissions_changed' }, status(403));
      const error = await done;
      expect((error as HttpErrorResponse).status).toBe(403);
      await settle();
      ctrl.expectNone('/auth/refresh');
      ctrl.expectNone('/api/notes');
    });

    it('permissions_changed on /auth/me itself does not ask /auth/me again', async () => {
      const done = lastValueFrom(http.get('/auth/me'));
      ctrl.expectOne('/auth/me').flush({ error: 'permissions_changed' }, status(403));
      await settle();
      ctrl.expectOne('/auth/refresh').flush({ access_token: 'new' });
      await settle();
      ctrl.expectOne('/auth/me').flush(adminMe);
      expect(await done).toEqual(adminMe);
    });
  });

  describe('503', () => {
    it('auth_unavailable: the bar, and the person stays signed in', async () => {
      const done = lastValueFrom(http.get('/api/notes')).catch((error: unknown) => error);
      ctrl.expectOne('/api/notes').flush({ error: 'auth_unavailable' }, status(503));
      const error = await done;
      expect((error as HttpErrorResponse).status).toBe(503);
      await settle();
      ctrl.expectNone('/auth/refresh');
      expect(auth.notice()).toBe('try_later');
      expect(auth.token()).toBe('old');
    });
  });

  describe('other answers', () => {
    it('pass through untouched: a 404, a 500 with a page of HTML, a 503 that is not auth_unavailable', async () => {
      for (const [code, body] of [
        [404, { error: 'not_found' }],
        [500, '<html>oops</html>'],
        [503, 'down for maintenance'],
      ] as const) {
        const done = lastValueFrom(http.get('/api/notes')).catch((error: unknown) => error);
        ctrl.expectOne('/api/notes').flush(body, status(code));
        expect(((await done) as HttpErrorResponse).status).toBe(code);
      }
      await settle();
      ctrl.expectNone('/auth/refresh');
      expect(auth.notice()).toBeNull();
    });

    it('a request that is not for the token gets none of this: a 401 from /auth/login reaches its caller', async () => {
      const done = lastValueFrom(http.post('/auth/login', {})).catch((error: unknown) => error);
      ctrl.expectOne('/auth/login').flush({ error: 'invalid_credentials' }, status(401));
      expect(((await done) as HttpErrorResponse).status).toBe(401);
      await settle();
      ctrl.expectNone('/auth/refresh');
    });
  });
});
