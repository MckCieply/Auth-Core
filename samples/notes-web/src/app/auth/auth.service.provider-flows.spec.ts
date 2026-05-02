import { HttpErrorResponse, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { adminMe, privateMe, providerMe, settle, signedInAs } from '../../testing/helpers';
import { authInterceptor, wantsToken } from './auth.interceptor';
import { AuthService, failureOf } from './auth.service';

const status = (code: number) => ({ status: code, statusText: String(code) });

describe('AuthService: the provider calls (spec 0011)', () => {
  let auth: AuthService;
  let ctrl: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(withInterceptors([authInterceptor])), provideHttpClientTesting()] });
    auth = TestBed.inject(AuthService);
    ctrl = TestBed.inject(HttpTestingController);
  });

  afterEach(() => ctrl.verify());

  it('the exchange that signs in starts a session, as a login does', async () => {
    const done = auth.exchangeTicket('t-1');
    const request = ctrl.expectOne('/auth/providers/exchange');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ ticket: 't-1' });
    expect(request.request.headers.has('Authorization')).toBe(false);
    request.flush({ status: 'authenticated', access_token: 'tok-p' });
    await settle();
    ctrl.expectOne('/auth/me').flush(providerMe);
    expect(await done).toEqual({ ok: true, next: null });
    expect(auth.token()).toBe('tok-p');
    expect(auth.me()).toEqual(providerMe);
  });

  it('reads next link and next finish_signup, and starts no session', async () => {
    const link = auth.exchangeTicket('t-1');
    ctrl.expectOne('/auth/providers/exchange').flush({ next: 'link', email: 'a@b.example', email_verified: false });
    expect(await link).toEqual({ ok: true, next: { next: 'link', email: 'a@b.example', emailVerified: false } });
    const verified = auth.exchangeTicket('t-1');
    ctrl.expectOne('/auth/providers/exchange').flush({ next: 'link', email: 'a@b.example', email_verified: true });
    expect(await verified).toEqual({ ok: true, next: { next: 'link', email: 'a@b.example', emailVerified: true } });
    const finish = auth.exchangeTicket('t-2');
    ctrl.expectOne('/auth/providers/exchange').flush({ next: 'finish_signup', email: 'a@b.example', kinds: ['private', 'organization', 'x'] });
    expect(await finish).toEqual({ ok: true, next: { next: 'finish_signup', email: 'a@b.example', kinds: ['organization', 'private'] } });
    expect(auth.token()).toBeNull();
    expect(auth.me()).toBeNull();
  });

  it.each([
    [['private'], ['private']],
    [['organization'], ['organization']],
    [['private', 'private', 'organization'], ['organization', 'private']],
  ])('the kinds %j read as %j', async (kinds, expected) => {
    const done = auth.exchangeTicket('t-1');
    ctrl.expectOne('/auth/providers/exchange').flush({ next: 'finish_signup', email: 'a@b.example', kinds });
    expect(await done).toEqual({ ok: true, next: { next: 'finish_signup', email: 'a@b.example', kinds: expected } });
  });

  it.each([
    [{ next: 'link', email: 'a@b.example' }],
    [{ next: 'link', email: 'a@b.example', email_verified: 'true' }],
    [{ next: 'link', email_verified: true }],
    [{ next: 'link', email: 5, email_verified: true }],
    [{ next: 'finish_signup', email: 'a@b.example', kinds: [] }],
    [{ next: 'finish_signup', email: 'a@b.example', kinds: ['x'] }],
    [{ next: 'finish_signup', email: 'a@b.example', kinds: 'organization' }],
    [{ next: 'finish_signup', email: 'a@b.example' }],
    [{ next: 'finish_signup', kinds: ['private'] }],
    [{ next: 'other' }],
    [{ next: null }],
    ['<html>'],
    [null],
    [{}],
    [{ status: 'other', access_token: 'tok' }],
    [{ status: 'authenticated' }],
    [{ status: 'authenticated', access_token: '' }],
  ])('an answer %j that is neither is other, and starts no session', async (body) => {
    const done = auth.exchangeTicket('t-1');
    ctrl.expectOne('/auth/providers/exchange').flush(body);
    expect(await done).toEqual({ ok: false, failure: { kind: 'other' } });
    expect(auth.token()).toBeNull();
  });

  it.each([
    [400, { error: 'invalid_ticket' }, { kind: 'invalid_ticket' }],
    [400, { error: 'invalid_token' }, { kind: 'invalid_token' }],
    [400, { error: 'invalid_request' }, { kind: 'other' }],
    [403, { error: 'no_account' }, { kind: 'no_account' }],
    [403, { error: 'no_email' }, { kind: 'no_email' }],
    [403, { error: 'address_mismatch' }, { kind: 'address_mismatch' }],
    [403, { error: 'no_membership' }, { kind: 'no_membership' }],
    [409, { error: 'already_registered' }, { kind: 'already_registered' }],
    [409, { error: 'already_member' }, { kind: 'already_member' }],
    [409, { error: 'private_account_exists' }, { kind: 'private_account_exists' }],
    [409, { error: 'provider_already_linked' }, { kind: 'provider_already_linked' }],
    [429, { error: 'too_many_requests', retry_after_seconds: 7 }, { kind: 'too_many_requests', retryAfterSeconds: 7 }],
    [500, 'oops', { kind: 'other' }],
    [0, null, { kind: 'other' }],
  ])('a refusal %s %j is the failure %j and leaves the session alone', async (code, body, failure) => {
    await signedInAs(auth, ctrl, adminMe, 'tok-old');
    const done = auth.exchangeTicket('t-1');
    const request = ctrl.expectOne('/auth/providers/exchange');
    if (code === 0) {
      request.error(new ProgressEvent('error'));
    } else {
      request.flush(body, status(code));
    }
    expect(await done).toEqual({ ok: false, failure });
    expect(auth.token()).toBe('tok-old');
    expect(auth.me()).toEqual(adminMe);
  });

  it('an answer with a next keeps the session that exists', async () => {
    await signedInAs(auth, ctrl, adminMe, 'tok-old');
    const done = auth.exchangeTicket('t-1');
    ctrl.expectOne('/auth/providers/exchange').flush({ next: 'link', email: 'a@b.example', email_verified: true });
    await done;
    expect(auth.token()).toBe('tok-old');
    expect(auth.me()).toEqual(adminMe);
  });

  it('the three provider calls never carry the access token of a signed-in person', async () => {
    await signedInAs(auth, ctrl, adminMe, 'tok-old');
    for (const path of ['/auth/providers/exchange', '/auth/providers/link', '/auth/providers/finish']) {
      expect(wantsToken(path)).toBe(false);
    }
    void auth.exchangeTicket('t-1');
    expect(ctrl.expectOne('/auth/providers/exchange').request.headers.has('Authorization')).toBe(false);
    void auth.linkProvider('t-1', 'Pw-1 x');
    expect(ctrl.expectOne('/auth/providers/link').request.headers.has('Authorization')).toBe(false);
    void auth.finishWithProvider('t-1', 'private', null);
    expect(ctrl.expectOne('/auth/providers/finish').request.headers.has('Authorization')).toBe(false);
  });

  it('the link sends the ticket and the password and signs in', async () => {
    const done = auth.linkProvider('t-1', 'Pw-1 x');
    const request = ctrl.expectOne('/auth/providers/link');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ ticket: 't-1', password: 'Pw-1 x' });
    expect(request.request.headers.has('Authorization')).toBe(false);
    request.flush({ status: 'authenticated', access_token: 'tok-l' });
    await settle();
    ctrl.expectOne('/auth/me').flush(privateMe);
    expect(await done).toEqual({ ok: true });
    expect(auth.token()).toBe('tok-l');
    expect(auth.me()).toEqual(privateMe);
  });

  it.each([
    [401, { error: 'invalid_credentials' }, { kind: 'invalid_credentials' }],
    [429, { error: 'too_many_attempts', retry_after_seconds: 90 }, { kind: 'too_many_attempts', retryAfterSeconds: 90 }],
    [429, { error: 'too_many_requests', retry_after_seconds: 4 }, { kind: 'too_many_requests', retryAfterSeconds: 4 }],
    [400, { error: 'invalid_ticket' }, { kind: 'invalid_ticket' }],
    [400, { error: 'invalid_request' }, { kind: 'other' }],
    [403, { error: 'email_not_verified' }, { kind: 'email_not_verified' }],
    [403, { error: 'no_membership' }, { kind: 'no_membership' }],
    [403, { error: 'no_email' }, { kind: 'no_email' }],
    [500, 'oops', { kind: 'other' }],
  ])('the link answered %s %j is the failure %j and starts no session', async (code, body, failure) => {
    const done = auth.linkProvider('t-1', 'Pw-1 x');
    ctrl.expectOne('/auth/providers/link').flush(body, status(code));
    expect(await done).toEqual({ ok: false, failure });
    expect(auth.token()).toBeNull();
  });

  it.each([[null], [{ status: 'other' }], [{ status: 'authenticated' }], ['<html>']])('a link answer %j that is no sign-in is other', async (body) => {
    const done = auth.linkProvider('t-1', 'Pw-1 x');
    ctrl.expectOne('/auth/providers/link').flush(body);
    expect(await done).toEqual({ ok: false, failure: { kind: 'other' } });
    expect(auth.token()).toBeNull();
  });

  it('the finish sends the kind, and the company name only for an organization', async () => {
    const organization = auth.finishWithProvider('t-1', 'organization', 'Firma');
    const first = ctrl.expectOne('/auth/providers/finish');
    expect(first.request.method).toBe('POST');
    expect(first.request.body).toEqual({ ticket: 't-1', kind: 'organization', company_name: 'Firma' });
    first.flush({ error: 'already_registered' }, status(409));
    expect(await organization).toEqual({ ok: false, failure: { kind: 'already_registered' } });
    const privatePerson = auth.finishWithProvider('t-2', 'private', null);
    const second = ctrl.expectOne('/auth/providers/finish');
    expect(second.request.body).toEqual({ ticket: 't-2', kind: 'private' });
    expect('company_name' in (second.request.body as object)).toBe(false);
    second.flush({ error: 'invalid_request' }, status(400));
    expect(await privatePerson).toEqual({ ok: false, failure: { kind: 'invalid_request' } });
  });

  it('the finish that succeeds signs in', async () => {
    const done = auth.finishWithProvider('t-1', 'private', null);
    ctrl.expectOne('/auth/providers/finish').flush({ status: 'authenticated', access_token: 'tok-f' });
    await settle();
    ctrl.expectOne('/auth/me').flush(providerMe);
    expect(await done).toEqual({ ok: true });
    expect(auth.token()).toBe('tok-f');
  });

  it.each([
    [409, { error: 'already_registered' }, { kind: 'already_registered' }],
    [403, { error: 'signup_disabled' }, { kind: 'signup_disabled' }],
    [400, { error: 'invalid_ticket' }, { kind: 'invalid_ticket' }],
    [400, { error: 'invalid_request' }, { kind: 'invalid_request' }],
    [403, { error: 'email_not_verified' }, { kind: 'email_not_verified' }],
    [429, { error: 'too_many_requests', retry_after_seconds: 4 }, { kind: 'too_many_requests', retryAfterSeconds: 4 }],
    [500, 'oops', { kind: 'other' }],
  ])('the finish answered %s %j is the failure %j and starts no session', async (code, body, failure) => {
    const done = auth.finishWithProvider('t-1', 'organization', 'Firma');
    ctrl.expectOne('/auth/providers/finish').flush(body, status(code));
    expect(await done).toEqual({ ok: false, failure });
    expect(auth.token()).toBeNull();
  });

  it('a failure of the contract with the wrong status is other', () => {
    expect(failureOf(new HttpErrorResponse({ status: 500, error: { error: 'invalid_ticket' } }))).toEqual({ kind: 'other' });
    expect(failureOf(new HttpErrorResponse({ status: 403, error: { error: 'invalid_ticket' } }))).toEqual({ kind: 'other' });
  });
});
