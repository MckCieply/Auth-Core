import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { adminMe, settle, signedInAs } from '../testing/helpers';
import { routes } from './app.routes';
import { AuthService } from './auth/auth.service';
import { LoginPage } from './pages/login';
import { NotesPage } from './pages/notes';

describe('routes', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideRouter(routes), provideHttpClient(), provideHttpClientTesting()] });
  });

  it.each(['/', '/notes', '/nowhere', '/notes/deeper/than/that'])(
    'an anonymous person who asks for %s lands on /login?returnUrl=%%2Fnotes',
    async (url) => {
      const harness = await RouterTestingHarness.create();
      await harness.navigateByUrl(url);
      expect(TestBed.inject(Router).url).toBe('/login?returnUrl=%2Fnotes');
      expect(harness.routeDebugElement?.componentInstance).toBeInstanceOf(LoginPage);
    },
  );

  it.each(['/', '/nowhere'])('a signed-in person who asks for %s lands on /notes', async (url) => {
    const ctrl = TestBed.inject(HttpTestingController);
    await signedInAs(TestBed.inject(AuthService), ctrl, adminMe);
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(url);
    ctrl.expectOne('/api/notes').flush([]);
    await settle(harness.fixture);
    expect(TestBed.inject(Router).url).toBe('/notes');
    expect(harness.routeDebugElement?.componentInstance).toBeInstanceOf(NotesPage);
  });

  it.each([
    ['/nowhere?token=abc', '/notes'],
    ['/nowhere?a=1&token=abc&b=2', '/notes?a=1&b=2'],
    ['/?token=abc', '/notes'],
  ])('a signed-in person who asks for %s lands on %s: a mail token is not carried on', async (url, expected) => {
    const ctrl = TestBed.inject(HttpTestingController);
    await signedInAs(TestBed.inject(AuthService), ctrl, adminMe);
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(url);
    ctrl.expectOne('/api/notes').flush([]);
    await settle(harness.fixture);
    expect(TestBed.inject(Router).url).toBe(expected);
  });

  it.each([
    ['/nowhere?token=abc', '/login?returnUrl=%2Fnotes'],
    ['/nowhere?a=1&token=abc', '/login?returnUrl=%2Fnotes%3Fa%3D1'],
    ['/notes?token=abc', '/login?returnUrl=%2Fnotes'],
    ['/?token=abc', '/login?returnUrl=%2Fnotes'],
  ])('an anonymous person who asks for %s lands on %s: no token in the address', async (url, expected) => {
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(url);
    expect(TestBed.inject(Router).url).toBe(expected);
  });

  it.each(['/login', '/forgot', '/reset', '/verify', '/invite'])('%s needs no token', async (url) => {
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(url);
    expect(TestBed.inject(Router).url).toBe(url);
  });
});
