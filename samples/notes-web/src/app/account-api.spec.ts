import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { AccountApi } from './account-api';

const status = (code: number) => ({ status: code, statusText: String(code) });

describe('AccountApi', () => {
  let api: AccountApi;
  let ctrl: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    api = TestBed.inject(AccountApi);
    ctrl = TestBed.inject(HttpTestingController);
  });

  afterEach(() => ctrl.verify());

  it.each([
    ['forgotPassword', () => api.forgotPassword('a@b.example'), '/auth/password/forgot', { email: 'a@b.example' }, 202],
    ['requestVerification', () => api.requestVerification('a@b.example'), '/auth/email/verify/request', { email: 'a@b.example' }, 202],
    ['resetPassword', () => api.resetPassword('tok', 'Pw-1 x'), '/auth/password/reset', { token: 'tok', new_password: 'Pw-1 x' }, 204],
    ['verifyEmail', () => api.verifyEmail('tok'), '/auth/email/verify', { token: 'tok' }, 204],
    ['acceptInvite', () => api.acceptInvite('tok', 'Pw-1 x'), '/auth/invites/accept', { token: 'tok', password: 'Pw-1 x' }, 204],
  ] as const)('%s posts to its endpoint with the body of the contract and is ok on the empty answer', async (_name, call, url, body, code) => {
    const done = call();
    const request = ctrl.expectOne(url);
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual(body);
    request.flush(null, status(code));
    expect((await done).ok).toBe(true);
  });

  it('reads the answer to a preview: the company, the email and the role', async () => {
    const done = api.previewInvite('tok');
    const request = ctrl.expectOne('/auth/invites/preview');
    expect(request.request.body).toEqual({ token: 'tok' });
    request.flush({ org_name: 'Acme', email: 'a@b.example', role: 'viewer' });
    expect(await done).toEqual({ ok: true, value: { org_name: 'Acme', email: 'a@b.example', role: 'viewer' } });
  });

  it('a preview that is not what the contract says is a failure, not a half-filled screen', async () => {
    const done = api.previewInvite('tok');
    ctrl.expectOne('/auth/invites/preview').flush({ org_name: 'Acme' });
    expect(await done).toEqual({ ok: false, failure: { kind: 'other' } });
  });

  it.each([
    [400, { error: 'invalid_token' }, { kind: 'invalid_token' }],
    [400, { error: 'weak_password', rules: ['too_short'] }, { kind: 'weak_password', rules: ['too_short'] }],
    [409, { error: 'already_member' }, { kind: 'already_member' }],
    [429, { error: 'too_many_attempts', retry_after_seconds: 42 }, { kind: 'too_many_attempts', retryAfterSeconds: 42 }],
    [400, { error: 'invalid_request' }, { kind: 'other' }],
    [500, '<html>oops</html>', { kind: 'other' }],
  ])('answer %s %j is the failure %j', async (code, body, failure) => {
    const done = api.acceptInvite('tok', 'pw');
    ctrl.expectOne('/auth/invites/accept').flush(body, status(code));
    expect(await done).toEqual({ ok: false, failure });
  });

  it('no network is a failure', async () => {
    const done = api.verifyEmail('tok');
    ctrl.expectOne('/auth/email/verify').error(new ProgressEvent('error'));
    expect(await done).toEqual({ ok: false, failure: { kind: 'other' } });
  });
});
