import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { adminMe, holdToken, privateMe, providerMe } from '../../testing/helpers';
import { AuthService, failureOf } from './auth.service';

function answer(status: number, error: string): HttpErrorResponse {
  return new HttpErrorResponse({ status, error: { error } });
}

describe('AuthService: /auth/me and the answers of spec 0011', () => {
  let auth: AuthService;
  let ctrl: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    auth = TestBed.inject(AuthService);
    ctrl = TestBed.inject(HttpTestingController);
  });

  async function meFor(body: object) {
    await holdToken(auth, ctrl, 'tok');
    const loaded = auth.loadMe();
    ctrl.expectOne('/auth/me').flush(body);
    await loaded;
    return auth.me();
  }

  it('reads has_password and linked_providers', async () => {
    expect(await meFor(providerMe)).toEqual(providerMe);
    expect(providerMe.has_password).toBe(false);
    expect(providerMe.linked_providers).toEqual(['google']);
  });

  it('the helpers of the earlier specs have a password and no provider', () => {
    expect([adminMe.has_password, privateMe.has_password]).toEqual([true, true]);
    expect([adminMe.linked_providers, privateMe.linked_providers]).toEqual([[], []]);
  });

  it.each([[undefined], [null], ['false'], [0]])('a has_password of %j reads as true: the screen keeps its password field', async (value) => {
    const body: Record<string, unknown> = { ...privateMe, has_password: value };
    if (value === undefined) {
      delete body['has_password'];
    }
    expect((await meFor(body))?.has_password).toBe(true);
  });

  it.each([
    [['google', 'github'], ['google']],
    [['microsoft', 'google'], ['microsoft', 'google']],
    ['google', []],
    [undefined, []],
  ])('linked_providers %j reads as %j', async (value, expected) => {
    expect((await meFor({ ...privateMe, linked_providers: value }))?.linked_providers).toEqual(expected);
  });

  it.each([
    [400, 'invalid_ticket'],
    [403, 'no_email'],
    [403, 'no_account'],
    [403, 'address_mismatch'],
    [409, 'provider_already_linked'],
    [403, 'reauth_failed'],
    [403, 'password_required'],
    [403, 'reauth_required'],
  ])('reads %s %s as a failure of its own', (status, code) => {
    expect(failureOf(answer(status, code))).toEqual({ kind: code });
  });

  it.each([
    [409, 'invalid_ticket'],
    [400, 'no_email'],
    [500, 'reauth_failed'],
  ])('reads %s %s as other: the status is part of the answer', (status, code) => {
    expect(failureOf(answer(status, code))).toEqual({ kind: 'other' });
  });
});
