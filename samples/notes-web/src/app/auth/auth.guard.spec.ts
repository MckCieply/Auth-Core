import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, Router, RouterStateSnapshot, UrlTree, provideRouter } from '@angular/router';
import { holdToken } from '../../testing/helpers';
import { AuthService } from './auth.service';
import { authGuard, safeReturnUrl } from './auth.guard';

describe('safeReturnUrl', () => {
  it.each([
    '//evil.example',
    '/\\evil.example',
    'https://evil.example',
    'http://evil.example/notes',
    'javascript:alert(1)',
    'evil.example',
    'notes',
    '',
    '/a\\b',
    '/%2F%2Fevil.example',
    '/%2fevil.example',
    '/%5Cevil.example',
    '/%5cevil.example',
    '/\t/evil.example',
    '/\n/evil.example',
    '/notes\r\nSet-Cookie: x=1',
    '/notes\rx',
    '/notes\x00',
    '/\x00notes',
    '/notes\x7f',
    '/' + 'a'.repeat(2048),
    '/' + 'a'.repeat(3000),
  ])('replaces %j by /notes', (value) => {
    expect(safeReturnUrl(value)).toBe('/notes');
  });

  it('replaces a missing value by /notes', () => {
    expect(safeReturnUrl(null)).toBe('/notes');
    expect(safeReturnUrl(undefined)).toBe('/notes');
  });

  it.each(['/notes', '/notes?x=1', '/notes/5?x=1&y=2#top', '/'])('keeps %s', (value) => {
    expect(safeReturnUrl(value)).toBe(value);
  });

  it('keeps a value of exactly 2048 characters and replaces one of 2049', () => {
    const longest = '/' + 'a'.repeat(2047);
    expect(safeReturnUrl(longest)).toBe(longest);
    expect(safeReturnUrl(longest + 'a')).toBe('/notes');
  });
});

describe('authGuard', () => {
  let auth: AuthService;
  let ctrl: HttpTestingController;
  let router: Router;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] });
    auth = TestBed.inject(AuthService);
    ctrl = TestBed.inject(HttpTestingController);
    router = TestBed.inject(Router);
  });

  const run = (url: string) =>
    TestBed.runInInjectionContext(() =>
      authGuard({} as ActivatedRouteSnapshot, { url } as RouterStateSnapshot),
    );

  it('sends an anonymous person to /login with the page they asked for', () => {
    const result = run('/notes?page=2');
    expect(result).toBeInstanceOf(UrlTree);
    expect(router.serializeUrl(result as UrlTree)).toBe('/login?returnUrl=%2Fnotes%3Fpage%3D2');
  });

  it('lets a person through when a token is held', async () => {
    await holdToken(auth, ctrl, 'tok');
    expect(run('/notes')).toBe(true);
  });

  it('sends a person to /login again once the token is dropped', async () => {
    await holdToken(auth, ctrl, 'tok');
    auth.dropSession();
    expect(run('/notes')).toBeInstanceOf(UrlTree);
  });
});
