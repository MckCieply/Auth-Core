import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { AccountApi } from './account-api';
import { AuthService } from './auth/auth.service';

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

    it.each([[{}], [{ access_token: '' }], [{ access_token: 5 }], ['<html>oops</html>'], [null]])(
      'a 200 with %j has no token: failed',
      async (body) => {
        const done = api.switchOrg(PRIVATE);
        ctrl.expectOne('/auth/switch').flush(body);
        expect(await done).toEqual({ result: 'failed' });
      },
    );
  });
});
