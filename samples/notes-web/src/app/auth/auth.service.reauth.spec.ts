import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { providerMe, signedInAs } from '../../testing/helpers';
import { AuthService } from './auth.service';

describe('AuthService.deleteAccountWithTicket (spec 0011)', () => {
  let auth: AuthService;
  let ctrl: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    auth = TestBed.inject(AuthService);
    ctrl = TestBed.inject(HttpTestingController);
    await signedInAs(auth, ctrl, providerMe);
  });

  afterEach(() => {
    ctrl.verify();
    sessionStorage.clear();
    localStorage.clear();
  });

  it('sends DELETE /auth/account with the provider ticket only, and the session ends on 204', async () => {
    const done = auth.deleteAccountWithTicket('r-1');
    const request = ctrl.expectOne('/auth/account');
    expect(request.request.method).toBe('DELETE');
    expect(request.request.body).toEqual({ provider_ticket: 'r-1' });
    request.flush(null, { status: 204, statusText: '204' });
    expect(await done).toEqual({ ok: true });
    expect(auth.token()).toBeNull();
    expect(auth.me()).toBeNull();
  });

  it('keeps the ticket out of the address, the query and the storage', async () => {
    const done = auth.deleteAccountWithTicket('r-secret-1');
    const request = ctrl.expectOne('/auth/account');
    expect(request.request.urlWithParams).toBe('/auth/account');
    expect(request.request.headers.keys()).not.toContain('x-ticket');
    expect(JSON.stringify({ ...sessionStorage }) + JSON.stringify({ ...localStorage })).not.toContain('r-secret-1');
    request.flush(null, { status: 204, statusText: '204' });
    await done;
    expect(JSON.stringify({ ...sessionStorage }) + JSON.stringify({ ...localStorage })).not.toContain('r-secret-1');
  });

  it.each(['reauth_failed', 'password_required', 'reauth_required'])('a 403 %s is a failure, and the session stays', async (code) => {
    const done = auth.deleteAccountWithTicket('r-1');
    ctrl.expectOne('/auth/account').flush({ error: code }, { status: 403, statusText: '403' });
    expect(await done).toEqual({ ok: false, failure: { kind: code } });
    expect(auth.token()).toBe('tok');
    expect(auth.me()).toEqual(providerMe);
  });

  it('a 429 is a wait, and the session stays', async () => {
    const done = auth.deleteAccountWithTicket('r-1');
    ctrl
      .expectOne('/auth/account')
      .flush({ error: 'too_many_requests', retry_after_seconds: 12 }, { status: 429, statusText: '429' });
    expect(await done).toEqual({ ok: false, failure: { kind: 'too_many_requests', retryAfterSeconds: 12 } });
    expect(auth.token()).toBe('tok');
  });

  it.each([
    [400, { error: 'invalid_request' }],
    [500, { error: 'server_error' }],
    [503, { error: 'auth_unavailable' }],
    [403, { error: 'forbidden' }],
  ])('a %i answer is "other", and the session stays', async (status, body) => {
    const done = auth.deleteAccountWithTicket('r-1');
    ctrl.expectOne('/auth/account').flush(body, { status, statusText: String(status) });
    expect(await done).toEqual({ ok: false, failure: { kind: 'other' } });
    expect(auth.token()).toBe('tok');
  });

  it('a network failure is "other", and the session stays', async () => {
    const done = auth.deleteAccountWithTicket('r-1');
    ctrl.expectOne('/auth/account').error(new ProgressEvent('error'));
    expect(await done).toEqual({ ok: false, failure: { kind: 'other' } });
    expect(auth.token()).toBe('tok');
  });
});
