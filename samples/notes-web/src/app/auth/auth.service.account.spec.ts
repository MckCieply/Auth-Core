import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { privateMe, signedInAs } from '../../testing/helpers';
import { wantsToken } from './auth.interceptor';
import { AuthService } from './auth.service';

const status = (code: number) => ({ status: code, statusText: String(code) });

describe('AuthService.deleteAccount (spec 0010)', () => {
  let auth: AuthService;
  let ctrl: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    auth = TestBed.inject(AuthService);
    ctrl = TestBed.inject(HttpTestingController);
  });

  it('sends DELETE /auth/account with the password and ends the session on 204', async () => {
    await signedInAs(auth, ctrl, privateMe);
    const done = auth.deleteAccount('Pw-1 x');
    const request = ctrl.expectOne('/auth/account');
    expect(request.request.method).toBe('DELETE');
    expect(request.request.body).toEqual({ password: 'Pw-1 x' });
    request.flush(null, status(204));
    expect(await done).toEqual({ ok: true });
    expect(auth.token()).toBeNull();
    expect(auth.me()).toBeNull();
  });

  it.each([
    [403, { error: 'wrong_password' }, { kind: 'wrong_password' }],
    [429, { error: 'too_many_attempts', retry_after_seconds: 90 }, { kind: 'too_many_attempts', retryAfterSeconds: 90 }],
    [403, { error: 'forbidden' }, { kind: 'other' }],
  ])('answer %s %j is the failure %j and the session stays', async (code, body, failure) => {
    await signedInAs(auth, ctrl, privateMe);
    const done = auth.deleteAccount('pw');
    ctrl.expectOne('/auth/account').flush(body, status(code));
    expect(await done).toEqual({ ok: false, failure });
    expect(auth.token()).not.toBeNull();
  });

  it('the interceptor gives the call the access token', () => {
    expect(wantsToken('/auth/account', 'https://app.example')).toBe(true);
  });
});
