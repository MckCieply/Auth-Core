import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { AccountApi } from './account-api';
import { AuthService, REFRESH_TIMEOUT_MS } from './auth/auth.service';

const status = (code: number) => ({ status: code, statusText: String(code) });
const ACME = '22222222-2222-2222-2222-222222222222';
const PRIVATE = '44444444-4444-4444-4444-444444444444';

describe('AccountApi: the spaces of the account (spec 0015)', () => {
  let api: AccountApi;
  let ctrl: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    api = TestBed.inject(AccountApi);
    ctrl = TestBed.inject(HttpTestingController);
  });

  afterEach(() => ctrl.verify());

  describe('listOrgs', () => {
    it('asks GET /auth/orgs and reads the entries', async () => {
      const done = api.listOrgs();
      const request = ctrl.expectOne('/auth/orgs');
      expect(request.request.method).toBe('GET');
      request.flush({
        orgs: [
          { id: ACME, name: 'Acme', kind: 'organization', role: { id: 'r1', name: 'admin' }, current: true },
          { id: PRIVATE, name: 'private', kind: 'personal', role: { id: 'r2', name: 'private' }, current: false },
        ],
      });
      expect(await done).toEqual({
        ok: true,
        value: [
          { id: ACME, name: 'Acme', kind: 'organization', role: { id: 'r1', name: 'admin' }, current: true },
          { id: PRIVATE, name: 'private', kind: 'personal', role: { id: 'r2', name: 'private' }, current: false },
        ],
      });
    });

    it.each([[{ orgs: 'x' }], [{}], [[]], ['<html>oops</html>'], [{ orgs: [{ id: ACME }] }], [{ orgs: [null] }]])(
      'the answer %j is not a list of spaces: a failure, never a crash',
      async (body) => {
        const done = api.listOrgs();
        ctrl.expectOne('/auth/orgs').flush(body);
        expect(await done).toEqual({ ok: false, failure: { kind: 'other' } });
      },
    );

    it('a refused call is a failure', async () => {
      const done = api.listOrgs();
      ctrl.expectOne('/auth/orgs').flush({ error: 'permissions_changed' }, status(403));
      expect((await done).ok).toBe(false);
    });
  });

  describe('switchOrg', () => {
    it('sends POST /auth/switch with the org_id, the way the refresh is sent, and reads the new token', async () => {
      const done = api.switchOrg(PRIVATE);
      const request = ctrl.expectOne('/auth/switch');
      expect(request.request.method).toBe('POST');
      expect(request.request.body).toEqual({ org_id: PRIVATE });
      expect(request.request.withCredentials).toBe(false);
      request.flush({ access_token: 'new-token' });
      expect(await done).toEqual({ result: 'ok', token: 'new-token' });
    });

    it('is sent with the credentials of the refresh: the same call options as /auth/refresh', async () => {
      const auth = TestBed.inject(AuthService);
      const refreshing = auth.refresh();
      const refresh = ctrl.expectOne('/auth/refresh');
      const switching = api.switchOrg(PRIVATE);
      const request = ctrl.expectOne('/auth/switch');
      expect(request.request.withCredentials).toBe(refresh.request.withCredentials);
      refresh.flush({ access_token: 'a' });
      request.flush({ access_token: 'b' });
      await Promise.all([refreshing, switching]);
    });

    it.each([
      [404, { error: 'not_found' }, { result: 'gone' }],
      [401, { error: 'invalid_grant' }, { result: 'rejected' }],
      [503, null, { result: 'failed' }],
      [429, { error: 'too_many_requests', retry_after_seconds: 5 }, { result: 'failed' }],
      [400, { error: 'invalid_request' }, { result: 'failed' }],
    ])('answer %s %j is %j', async (code, body, outcome) => {
      const done = api.switchOrg(PRIVATE);
      ctrl.expectOne('/auth/switch').flush(body, status(code));
      expect(await done).toEqual(outcome);
    });

    it('an answer that does not come within ten seconds is a failure, and the request is cancelled', async () => {
      vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval'] }); // the timeout of rxjs runs on setInterval
      try {
        const done = api.switchOrg(PRIVATE);
        const request = ctrl.expectOne('/auth/switch');
        expect(REFRESH_TIMEOUT_MS).toBe(10_000);
        vi.advanceTimersByTime(REFRESH_TIMEOUT_MS - 1);
        expect(request.cancelled).toBe(false);
        vi.advanceTimersByTime(2);
        expect(await done).toEqual({ result: 'failed' });
        expect(request.cancelled).toBe(true);
      } finally {
        vi.useRealTimers();
      }
    });

    it.each([[{}], [{ access_token: '' }], [{ access_token: 5 }], ['<html>oops</html>'], [null]])(
      'a 200 with %j has no token: failed',
      async (body) => {
        const done = api.switchOrg(PRIVATE);
        ctrl.expectOne('/auth/switch').flush(body);
        expect(await done).toEqual({ result: 'failed' });
      },
    );
  });

  describe('createOrg (spec 0016)', () => {
    const entry = { id: ACME, name: 'Gamma', kind: 'organization', role: { id: 'r1', name: 'admin' }, current: false };

    it('createOrg_posts_the_name_with_the_token_and_returns_the_entry', async () => {
      const done = api.createOrg('Gamma');
      const request = ctrl.expectOne('/auth/orgs');
      expect(request.request.method).toBe('POST');
      expect(request.request.body).toEqual({ name: 'Gamma' });
      request.flush(entry, status(201));
      expect(await done).toEqual({ ok: true, value: entry });
    });

    it.each([[{}], ['<html>oops</html>'], [{ id: ACME }], [null]])('a 201 with %j is not an entry: a failure', async (body) => {
      const done = api.createOrg('Gamma');
      ctrl.expectOne('/auth/orgs').flush(body, status(201));
      expect(await done).toEqual({ ok: false, failure: { kind: 'other' } });
    });

    it.each([
      [403, 'org_creation_disabled', 'org_creation_disabled'],
      [409, 'company_limit_reached', 'company_limit_reached'],
      [403, 'company_member', 'company_member'],
      [403, 'permissions_changed', 'permissions_changed'],
      [400, 'invalid_request', 'invalid_request'],
      [500, 'boom', 'other'],
    ])('createOrg_carries_the_error_codes: %s %s is %s', async (code, error, kind) => {
      const done = api.createOrg('Gamma');
      ctrl.expectOne('/auth/orgs').flush({ error }, status(code));
      expect(await done).toEqual({ ok: false, failure: { kind } });
    });
  });

  describe('options (spec 0016)', () => {
    it.each([
      ['self-service', true],
      ['off', false],
      ['on', false],
    ])('the_options_body_with_org_creation_%s_is_read: orgCreation %s', async (value, expected) => {
      const done = api.options();
      ctrl.expectOne('/auth/options').flush({ signup: 'self-service', private_signup: 'off', providers: [], org_creation: value });
      expect((await done).orgCreation).toBe(expected);
    });

    it('an_old_body_without_it_means_off', async () => {
      const done = api.options();
      ctrl.expectOne('/auth/options').flush({ signup: 'self-service', private_signup: 'off', providers: [] });
      expect((await done).orgCreation).toBe(false);
    });

    it('a_failed_call_means_off', async () => {
      const done = api.options();
      ctrl.expectOne('/auth/options').flush('oops', status(500));
      expect((await done).orgCreation).toBe(false);
    });
  });
});
